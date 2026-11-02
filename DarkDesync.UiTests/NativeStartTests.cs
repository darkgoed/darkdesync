using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using DarkDesync;
using DarkDesync.Core;
using DarkDesync.Models;
using DarkDesync.Services;
using DarkDesync.ViewModels;

internal static partial class Program
{
    private static Application CreateTestApplication()
    {
        // Load the actual shared theme without App.StartupUri automatically
        // creating a second window with the default (non-test) settings store.
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/DarkDesync;component/Themes/DarkTheme.xaml", UriKind.Absolute)
        });
        return app;
    }

    // Driver-free smoke also exercises the real Button's OnClick/command binding,
    // not ICommand.Execute. Native suite below additionally uses UIA client lookup.
    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        Pump();
    }

    private static void RouteSpace(System.Windows.Controls.Primitives.ButtonBase button)
    {
        button.Focus(); Pump();
        Require(button.IsKeyboardFocused, "Real loaded START/STOP button has WPF keyboard focus");
        var source = PresentationSource.FromVisual(button)!;
        var down = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, System.Windows.Input.Key.Space)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        button.RaiseEvent(down);
        if (!down.Handled) { down.RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent; button.RaiseEvent(down); }
        Pump();
        var up = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, System.Windows.Input.Key.Space)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyUpEvent };
        button.RaiseEvent(up);
        if (!up.Handled) { up.RoutedEvent = System.Windows.Input.Keyboard.KeyUpEvent; button.RaiseEvent(up); }
        Pump();
    }

    private static int RunNativeUi()
    {
        if (Environment.GetEnvironmentVariable("DARKDESYNC_DRIVER_TESTS") != "1" ||
            Environment.GetEnvironmentVariable("DARKDESYNC_GLOBAL_TESTS") != "1")
            throw new InvalidOperationException("Native UI requires isolated-runner driver AND GLOBAL consent.");
        string dir = Path.Combine(Path.GetTempPath(), "DarkDesync-NativeUI-" + Guid.NewGuid());
        MainWindow? window = null;

        try
        {
            var app = CreateTestApplication();
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            window = new MainWindow(store); app.MainWindow = window;
            window.Show(); Pump();
            var vm = (MainViewModel)window.DataContext;
            var engine = (PacketEngine)typeof(MainViewModel).GetField("_packetEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            var hotkey = (HotkeyService)typeof(MainViewModel).GetField("_hotkeyService", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            var controls = Descendants(window).ToArray();
            var effects = controls.OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, vm.DiagnosticModeOptions));
            var enabled = controls.OfType<CheckBox>().Single(c => Equals(c.Content, "Habilitado"));
            var status = (TextBlock)window.FindName("StartStatus");
            var state = (TextBlock)window.FindName("CaptureState");
            var start = (Button)window.FindName("StartStopButton");
            var pulse = controls.OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.TriggerCycleCommand));
            Require(window.FindName("PacketSizeConsent") == null, "No per-mode consent checkbox");
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            using var automation = new NativeUiaClient();
            AutomationElement? nativeStart = null; // Owned/accessed only by the MTA client below.
            void ClickStart()
            {
                var click = automation.InvokeAsync(() =>
                {
                    nativeStart ??= AutomationElement.FromHandle(hwnd).FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "StartStopButton"));
                    Require(nativeStart != null && nativeStart.Current.IsEnabled, "UIA discovers enabled START/STOP");
                    ((InvokePattern)nativeStart!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                });
                Await(click); Pump();
            }
            void Select(DiagnosticMode mode)
            {
                effects.SelectedItem = ModeCatalog.Name(mode); Pump();
                Require(ModeCatalog.Parse(vm.SelectedDiagnosticModeText) == mode && vm.SelectedProcess == null, "Ranked effect identity, no process selection");
                Require(vm.IsLegacyGlobalSelected && engine.ConfiguredFilterMode == FilterMode.LegacyGlobalOutbound, "Only global outbound is applied");
            }
            void Stopped()
            {
                Require(!engine.IsCaptureRequested && !engine.IsRunning && Equals(start.Content, "START") && state.Text == "PARADO", "STOP/refusal must not fake arming");
            }
            void Armed()
            {
                Require(engine.IsCaptureRequested && engine.IsRunning && Equals(start.Content, "STOP") && state.Text == "ARMADO", "Real driver, requested/running and conspicuous ARMADO state agree");
                Require(engine.CurrentFilter == "outbound" && vm.SelectedProcess == null, "Literal outbound without a target");
                Require(!engine.IsEffectActive && engine.ActiveDiagnosticMode == DiagnosticMode.None, "START only arms, never triggers effect");
                CheckStateColor(window, "ARMADO", "#06D6A0");
            }
            Require(controls.OfType<ComboBox>().Count() == 2 && vm.Processes.Count == 0, "Only effect/style/hotkey; no network/protocol/target discovery or selectors");
            Require(vm.DiagnosticCycleDurationMs == 200 && state.Text == "PARADO", "Initial stopped state; ranking does not set 250ms");
            VerifyExperienceUi(window, vm, store, engine);
            VerifyCompactPanelUi(window, vm, engine, "artifacts/driver/screenshots");
            foreach (var mode in ModeCatalog.OutboundChoices.Values.Where(m => m != DiagnosticMode.PacketSizeAwareShaping))
            {
                Select(mode); enabled.IsChecked = true; Pump(); ClickStart(); Armed();
                CapturePanel(window, "artifacts/driver/screenshots", "native-armed-" + mode);
                int beforeStyle = vm.DiagnosticCycleDurationMs;
                RouteSpace((RadioButton)window.FindName("AggressiveStyleButton"));
                Require(vm.DiagnosticCycleDurationMs == beforeStyle && !engine.IsEffectActive, "Focused radio Space cannot select a preset or double-pulse");
                Require(!vm.GlobalPacketSizeConsent && !vm.ExperimentalOptIn, "Four favorites need no extra opt-in");
                RouteSpace(start); Armed(); // Routed WPF keys must not click focused STOP.
                Await(TcpLoopbackProbe());
                enabled.IsChecked = false; Pump(); InvokeButton(pulse);
                Require(!engine.IsEffectActive && state.Text == "ARMADO", "Disabled effect keeps capture armed but cannot pulse");
                enabled.IsChecked = true; vm.DiagnosticCycleDurationMs = 250; Pump();
                hotkey.DispatchKeyTransition(0x20, true); Pump();
                Require(engine.IsEffectActive, "Hotkey subscriber pulses the native capture (not a hardware-key claim)");
                RouteSpace(start); Require(engine.IsRunning && engine.IsCaptureRequested, "Focused Space never stops native capture");
                hotkey.DispatchKeyTransition(0x20, false); Until(() => !engine.IsEffectActive);
                Await(TcpLoopbackProbe());
                using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                using var sender = new UdpClient(); sender.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
                long held = engine.PacketsThrottled;
                vm.IsManualDurationEnabled = true; vm.DiagnosticCycleDurationMs = 800; Pump();
                InvokeButton(pulse); Require(engine.IsEffectActive && state.Text == "PULSO ATIVO", "Pulse state is distinct from capture arming");
                CheckStateColor(window, "PULSO ATIVO", "#EF233C");
                CapturePanel(window, "artifacts/driver/screenshots", "native-pulse-" + mode);
                if (mode == DiagnosticMode.GapBasedReordering)
                {
                    Await(TcpLoopbackProbe());
                    Require(engine.IsEffectActive, "Gap TCP pass-through probe must finish inside pulse");
                }
                long sentBefore = engine.PacketsPassedThrough, lossBefore = engine.IntentionalBurstDrops;
                Await(sender.SendAsync(new byte[900], 900));
                Until(() => engine.PacketsThrottled > held || engine.PacketsPassedThrough > sentBefore || engine.IntentionalBurstDrops > lossBefore);
                ClickStart(); Stopped();
                if (engine.IntentionalBurstDrops == lossBefore)
                    Require(Await(receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer.Length == 900, "STOP drains own UDP or GE passes");
                Await(Task.Delay(40)); Require(receiver.Available == 0, "No duplication");
                if (mode != DiagnosticMode.GilbertElliottBurstLoss) Require(engine.PacketsDropped == 0, "Non-loss profile cannot discard");
                Console.WriteLine("PASS outbound-only UIA/native: " + mode + "; no PID; enabled START; ARMADO/PULSO/PARADO; exact outbound; own TCP/UDP; focused Space; STOP drain");
            }
            Console.WriteLine("PASS twelve exposed effects; PacketSizeAware remains legacy-only and is tested outside production UI.");
            foreach (var oldFilter in Enum.GetValues<FilterMode>())
            {
                store.Save(new AppSettings { FilterMode = oldFilter, DiagnosticMode = DiagnosticMode.DualTokenBucket, DiagnosticDurationMs = 175, OnlyTargetForeground = true });
                byte[] before = File.ReadAllBytes(store.FilePath); vm.LoadSettings(); Pump();
                Require(!engine.IsCaptureRequested && vm.SelectedProcess == null && vm.DiagnosticCycleDurationMs == 175, "Old scope migration never auto-starts or overrides duration");
                enabled.IsChecked = true; Pump(); ClickStart(); Armed(); ClickStart(); Stopped();
                Require(before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Real START/STOP does not rewrite old scoped settings");
                Console.WriteLine("PASS outbound-only old scope UIA/native: " + oldFilter + "; no target; read-only migration; START/STOP");
            }
            foreach (var hidden in ModeCatalog.Choices.Values.Except(ModeCatalog.OutboundChoices.Values))
            {
                store.Save(new AppSettings { DiagnosticMode = hidden, FilterMode = FilterMode.AllTraffic, DiagnosticDurationMs = 250 });
                byte[] before = File.ReadAllBytes(store.FilePath); vm.LoadSettings(); Pump();
                Require(effects.SelectedItem == null && vm.SettingsStatus.Contains("indisponível"), "Hidden saved mode needs deliberate replacement");
                ClickStart(); Stopped(); Require((status.Text.Contains("indisponível") && vm.StartStatusText.Contains("INCOMPATÍVEL")), "No fake global equivalent or unsafe reordering");
                Require(before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Hidden configuration remains untouched");
            }
            Select(DiagnosticMode.DualTokenBucket);
            vm.CustomFilterText = "203.0.113.88:4567 secret-token";
            foreach (int code in new[] { 2, 5, 87, 577 })
            {
                engine.NetworkOpenFailureOverride = f => { Require(f == "outbound", "Custom/legacy config cannot widen applied filter"); return code; };
                ClickStart(); Stopped();
                Require(status.Text.Contains("Win32 " + code) && vm.SupportDiagnosticText.Contains("Win32=" + code), "Compact native error and internal sanitized evidence");
                CheckStateColor(window, "PARADO", "#8D93AA");
                CapturePanel(window, "artifacts/driver/screenshots", "start-negative-win32-" + code);
            }
            engine.NetworkOpenFailureOverride = null;
            Require(window.FindName("CopyDiagnosticButton") == null, "No diagnostic copy UI"); vm.CopyDiagnosticCommand.Execute(null);
            Require(Clipboard.GetText() == vm.SupportDiagnosticText, "Explicit Clipboard read-back");
            Require(!Clipboard.GetText().Contains("203.0.113") && !Clipboard.GetText().Contains("secret-token") && !Clipboard.GetText().Contains("PID") && !Clipboard.GetText().Contains("FLOW"), "No sensitive values or target prerequisites in support text");
            Console.WriteLine("PASS outbound-only native UI: all twelve exposed effects; real UIA START/STOP without target; read-only legacy migration; hidden modes refused; Win32 failure injection; Clipboard. Physical keyboard and user's PC NOT tested.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            window?.Close(); Pump();

            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static void Until(Func<bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        do { Pump(); if (predicate()) return; Thread.Sleep(10); } while (clock.Elapsed < TimeSpan.FromSeconds(10));
        throw new TimeoutException("Native UI condition did not become true");
    }
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); Pump(); }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
    private static async Task TcpLoopbackProbe()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient();
        var accepted = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accepted;
        byte[] payload = { 73 }; await client.GetStream().WriteAsync(payload);
        var received = new byte[1]; await server.GetStream().ReadExactlyAsync(received);
        Require(received.SequenceEqual(payload), "Owned loopback TCP healthy");
    }
}
