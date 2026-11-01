using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using DarkDesync.Core;
using DarkDesync.Services;
using DarkDesync.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    [STAThread]
    public static int Main(string[] args)
    {
        string settingsDirectory = Path.Combine(Path.GetTempPath(), "DarkDesyncUi-" + Guid.NewGuid());
        try
        {
            // Safety guard BEFORE constructing an engine: never execute legacy auto-start.
            var source = File.ReadAllText(Path.Combine(Root, "DarkDesync/ViewModels/MainViewModel.cs"));
            var ctor = source.Substring(source.IndexOf("public MainViewModel("));
            ctor = ctor[..ctor.IndexOf("public void EnsureEngineRunning")];
            Check(!ctor[ctor.LastIndexOf("RefreshProcessList();")..].Contains("EnsureEngineRunning();"), "constructor has no automatic capture startup");
            var apply = source.Substring(source.IndexOf("private void ApplyFilterMode()"));
            apply = apply[..apply.IndexOf("private void ExecuteTriggerCycle")];
            Check(!apply.Contains("EnsureEngineRunning();"), "filter edits cannot start capture");
            var state = new DesyncStateMachine();
            using var engine = new PacketEngine(NullLogger<PacketEngine>.Instance,
                new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance),
                new FilterBuilder(NullLogger<FilterBuilder>.Instance), new FlowTracker(NullLogger<FlowTracker>.Instance), state);
            using var controller = new DesyncController(NullLogger<DesyncController>.Instance, state, engine);
            using var vm = new MainViewModel(new ProcessService(NullLogger<ProcessService>.Instance),
                new HotkeyService(NullLogger<HotkeyService>.Instance), controller, engine,
                new SettingsStore(Path.Combine(settingsDirectory, "settings.json")));
            Check(!engine.IsRunning && !vm.IsEngineRunning, "STA construction leaves real engine stopped without driver");
            var timer = (System.Windows.Threading.DispatcherTimer)typeof(MainViewModel).GetField("_statsTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            Check(!timer.IsEnabled, "hidden telemetry does not poll the UI");
            vm.SelectedFilterModeText = "Personalizado"; vm.CustomFilterText = "inbound";
            Check(!engine.IsRunning && !vm.IsEngineRunning, "editing custom filter leaves engine stopped");
            var jitter = typeof(MainViewModel).GetProperty("ThrottleJitterMs");
            Check(jitter != null, "jitter view-model property exists");
            var engineJitter = typeof(PacketEngine).GetProperty("ThrottleJitterMs");
            Check(engineJitter != null, "jitter engine property exists");
            foreach (var (input, expected) in new[] { (-5, 0), (320, 320), (9999, 2000), (0, 0) })
            {
                jitter!.SetValue(vm, input);
                Check((int)jitter.GetValue(vm)! == expected && (int)engineJitter!.GetValue(engine)! == expected, $"jitter {input} clamps/propagates to {expected}");
            }
            Check(source.Contains("_packetEngine.ThrottleJitterMs = ThrottleJitterMs;"), "explicit startup synchronizes jitter");
            // Exercise startup synchronization without permitting a native Start call.
            // A temporary internal running sentinel takes only the already-running branch;
            // no handle, worker, driver or traffic is created. Restore before disposal.
            jitter!.SetValue(vm, 610);
            engineJitter!.SetValue(engine, 12);
            var running = typeof(PacketEngine).GetField("_isRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                running.SetValue(engine, true);
                vm.EnsureEngineRunning();
                Check((int)engineJitter.GetValue(engine)! == 610, "EnsureEngineRunning reapplies jitter without native Start");
            }
            finally { running.SetValue(engine, false); }
            jitter.SetValue(vm, 0);
            if (args.Contains("--render")) Render(vm);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, true); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static bool engineMode(MainViewModel vm) => vm.SelectedThrottleMode == vm.ThrottleModeOptions[1];
    private static void Render(MainViewModel vm)
    {
        // Parse the production XAML without x:Class: no MainWindow constructor,
        // native HWND, Loaded hook, global key registration or driver initialization.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var theme = (ResourceDictionary)XamlReader.Load(File.OpenRead(Path.Combine(Root, "DarkDesync/Themes/DarkTheme.xaml")));
        app.Resources.MergedDictionaries.Add(theme);
        var document = XDocument.Load(Path.Combine(Root, "DarkDesync/MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        document.Root!.Attribute(x + "Class")!.Remove();
        // Strip code-behind events only; all markup and bindings remain real.
        foreach (var element in document.Descendants())
            foreach (var attr in element.Attributes().Where(a => new[] { "Click", "MouseLeftButtonDown" }.Contains(a.Name.LocalName)).ToList()) attr.Remove();
        var window = (Window)XamlReader.Parse(document.ToString());
        window.DataContext = vm;
        var content = (FrameworkElement)window.Content;
        window.Content = null; // off-screen, not shown and cannot fire Loaded hooks
        content.DataContext = vm;
        content.Resources.MergedDictionaries.Add(theme);
        foreach (var type in new[] { typeof(ComboBox), typeof(CheckBox), typeof(TextBox), typeof(Slider), typeof(TabControl), typeof(TabItem), typeof(GridViewColumnHeader) })
            Check(theme.Contains(type), "custom dark template: " + type.Name);
        var size = new Size(window.Width, window.Height);
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        var sliders = Descendants(content).OfType<Slider>().ToList();
        Check(sliders.Count == 10, "ten configuration sliders including diagnostic modes, no metric panels");
        var jitterSlider = sliders.Single(s => System.Windows.Data.BindingOperations.GetBinding(s, Slider.ValueProperty)?.Path.Path == "ThrottleJitterMs");
        jitterSlider.Value = 470;
        Check(vm.ThrottleJitterMs == 470, "real XAML jitter slider updates view-model two-way");
        vm.ThrottleJitterMs = 230;
        Check(jitterSlider.Value == 230, "view-model jitter updates real XAML slider");
        vm.ThrottleJitterMs = 0;
        Check(!document.ToString().Contains("Countdown") && !document.ToString().Contains("{Binding Packets") && !document.ToString().Contains("{Binding History}"), "no countdown, counters or metric history bindings");
        var combos = Descendants(content).OfType<ComboBox>().ToList();
        // Processes combo is populated at runtime; in headless rendering no GUI processes are visible so Count=0 is expected.
        Check(combos.Count == 7 && combos.Where(c => System.Windows.Data.BindingOperations.GetBinding(c, ComboBox.ItemsSourceProperty)?.Path.Path != "Processes").All(c => c.Items.Count > 0), "target, filter, hotkey, throttle mode and diagnostic hotkey lists resolve real items bindings");
        var mode = combos.Single(c => System.Windows.Data.BindingOperations.GetBinding(c, ComboBox.SelectedItemProperty)?.Path.Path == "SelectedThrottleMode");
        mode.SelectedItem = vm.ThrottleModeOptions[1];
        Check(engineMode(vm), "real mode selector updates view-model");
        mode.SelectedItem = vm.ThrottleModeOptions[0];
        var profile = Descendants(content).OfType<CheckBox>().Single(c => System.Windows.Data.BindingOperations.GetBinding(c, CheckBox.IsCheckedProperty)?.Path.Path == "CycleProfileEnabled");
        profile.IsChecked = false; Check(!vm.CycleProfileEnabled, "real cycle profile toggle updates view-model");
        profile.IsChecked = true;
        var numbers = Descendants(content).OfType<TextBox>().Where(t => System.Windows.Data.BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path != "CustomFilterText").ToList();
        Check(numbers.Count == 11, "eleven editable settings alongside sliders");
        foreach (var combo in combos)
        {
            var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
            var surface = (Border)popup.Child;
            Check(((SolidColorBrush)surface.Background).Color == (Color)ColorConverter.ConvertFromString("#20232D"), "real ComboBox dropdown uses dark surface");
        }
        Check(Descendants(content).OfType<Button>().Count(b => b.Command != null) == 13, "capture, trigger, cancel, refresh, presets, diagnostic triggers and three settings commands resolve");
        Check(Descendants(content).OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.TriggerFreezeCommand)), "real freeze button binds command");
        Check(Descendants(content).OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.TriggerBurstCommand)), "real burst button binds command");
        Check(Descendants(content).OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.TriggerAsymmetricCommand)), "real asymmetric button binds command");
        Check(Descendants(content).OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.SaveSettingsCommand)), "real save button binds save command");
        var saveButton = Descendants(content).OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.SaveSettingsCommand));
        var saveOrigin = saveButton.TransformToAncestor(content).Transform(new Point());
        Check(saveOrigin.Y >= 0 && saveOrigin.Y + saveButton.ActualHeight < size.Height - 36, "save button visible without scrolling");
        var durationInput = numbers.Single(t => System.Windows.Data.BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path == "CustomDurationMs");
        durationInput.Text = "1250"; durationInput.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Check(vm.CustomDurationMs == 1250, "numeric duration input updates real setting");
        vm.CustomDurationMs = 800;
        saveButton.Command.Execute(null);
        Check(File.Exists(vm.SettingsPath) && new SettingsStore(vm.SettingsPath).Load().DurationMs == 800, "real save button command writes verified isolated JSON");
        vm.ThrottleJitterMs = 600;
        Descendants(content).OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.LoadSettingsCommand)).Command.Execute(null);
        Check(vm.ThrottleJitterMs == 0 && !vm.IsEngineRunning, "real load button command restores JSON without capture");
        var expander = Descendants(content).OfType<Expander>().Single();
        expander.IsExpanded = true; content.UpdateLayout();
        Check(Descendants(expander).OfType<ListBox>().Single().Items.Count == vm.Logs.Count, "diagnostic expander binds real event logs");
        expander.IsExpanded = false;
        vm.SelectedFilterModeText = vm.FilterModeOptions[0];
        vm.SelectedProcess = vm.Processes.FirstOrDefault();
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        string path = Path.Combine(Root, "DarkDesync/visual-verification.png");
        using (var stream = File.Create(path)) encoder.Save(stream);
        Check(File.Exists(path) && new FileInfo(path).Length > 10000, "real WPF render saved: " + path);
        window.Close(); app.Shutdown();
    }
}
