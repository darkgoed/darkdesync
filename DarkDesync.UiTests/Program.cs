using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DarkDesync;
using DarkDesync.Core;
using DarkDesync.Services;
using DarkDesync.ViewModels;

// Windows/STA visual-tree and binding smoke test. No packet capture is started.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if ((args.Length == 3 || args.Length == 4) && args[0] == "--udp-peer") return RunUdpPeer(args);
        string temporary = Path.Combine(Path.GetTempPath(), "DarkDesync-UI-" + Guid.NewGuid());
        var errors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            var app = new App(); app.InitializeComponent();
            var window = new MainWindow(new SettingsStore(Path.Combine(temporary, "settings.json")));
            var vm = (MainViewModel)window.DataContext;
            window.Show(); Pump();
            Require(vm.SelectedHotkeyName == "Espaço", "Default global hotkey");
            Require(!vm.IsEngineRunning, "Opening UI must not start capture");
            var diagnostics = (TextBlock)window.FindName("NetworkScopeDiagnostics");
            Require(diagnostics.Text.Contains("TCP:") && diagnostics.Text.Contains("UDP:"), "Protocol diagnosis binding");
            Require(diagnostics.Text.Contains("antes de entrar na experiência"), "Explicit pre-experience observation guidance");
            var controls = Descendants(window).ToArray();
            var mode = controls.OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, vm.DiagnosticModeOptions));
            Require(controls.OfType<ComboBox>().Count(c => ReferenceEquals(c.ItemsSource, vm.AvailableHotkeys.Keys)) <= 1, "One hotkey picker");
            foreach (string effect in new[] { "Outbound Freeze", "Burst Loss", "Asymmetric Loss" })
            {
                mode.SelectedItem = effect; Pump();
                Require(vm.SelectedDiagnosticModeText == effect, "Mode selection binding: " + effect);
                Require(vm.IsDiagnosticModeSelected && !vm.IsLegacyModeSelected, "Unified profile visibility");
                var duration = controls.OfType<Slider>().Single(s => s.Maximum == 800 && s.Minimum == 0);
                duration.Value = 175; Pump();
                Require(vm.DiagnosticCycleDurationMs == 175, "Duration slider binding");
                Require(duration.IsVisible, "Diagnostic duration visible inside profile");
                Require(vm.IsBurstLossVisible == (effect == "Burst Loss"), "Burst controls visibility");
                Require(vm.IsAsymmetricLossVisible == (effect == "Asymmetric Loss"), "Asymmetric controls visibility");
            }
            vm.BurstLossPercentage = 37; vm.AsymmetricInboundLossPercentage = 12; vm.AsymmetricOutboundLossPercentage = 84;
            vm.SelectedHotkeyName = "F2"; vm.DiagnosticCycleDurationMs = 800;
            vm.SaveSettingsCommand.Execute(null);
            vm.ResetSettingsCommand.Execute(null); Pump();
            Require(vm.DiagnosticCycleDurationMs == 150, "Default 150 ms");
            vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.DiagnosticCycleDurationMs == 800 && vm.SelectedDiagnosticModeText == "Asymmetric Loss", "Mode/duration roundtrip");
            Require(vm.BurstLossPercentage == 37 && vm.AsymmetricInboundLossPercentage == 12 && vm.AsymmetricOutboundLossPercentage == 84, "Loss percentages roundtrip");
            Require(vm.SelectedHotkeyName == "F2", "Shared hotkey roundtrip");
            vm.DiagnosticCycleDurationMs = 0; vm.TriggerCycleCommand.Execute(null); Pump();
            Require(!vm.IsEngineRunning, "Zero-duration pulse must not start capture");
            Require(controls.OfType<TextBox>().All(t => !t.Text.Contains("127.0.0.1")), "No manual endpoint input");
            Require(controls.OfType<Button>().Count(b => ReferenceEquals(b.Command, vm.TriggerCycleCommand)) == 1, "Exactly one pulse button");
            vm.ResetSettingsCommand.Execute(null); Pump();
            Require(!vm.IsLegacyGlobalSelected && !vm.LegacyGlobalConsent, "Default is strict, no global opt-in");
            vm.SelectedFilterModeText = "Outbound legado — todos os aplicativos (global)"; Pump();
            var consent = (CheckBox)window.FindName("GlobalLegacyConsent");
            var warning = (TextBlock)window.FindName("GlobalLegacyWarning");
            Require(warning.IsVisible && warning.Text.Contains("TODAS") && warning.Text.Contains("Windows"), "GLOBAL warning must be explicit");
            Require(consent.IsChecked != true && !vm.LegacyGlobalConsent, "Selection is not consent");
            vm.TriggerCycleCommand.Execute(null); vm.ToggleEngineCommand.Execute(null); Pump();
            Require(!vm.IsEngineRunning && vm.NetworkDiagnosticsText.Contains("GLOBAL"), "No-consent/continuous global must not open capture");
            consent.IsChecked = true; Pump();
            Require(vm.LegacyGlobalConsent && vm.DiagnosticCycleDurationMs == 150, "Explicit opt-in binding and default pulse duration");
            vm.DiagnosticCycleDurationMs = 0; vm.TriggerCycleCommand.Execute(null); vm.CancelCycleCommand.Execute(null); Pump();
            Require(!vm.IsEngineRunning, "GLOBAL zero pulse and Cancel do not capture");
            vm.SaveSettingsCommand.Execute(null); vm.LoadSettingsCommand.Execute(null); Pump();
            Require(!vm.LegacyGlobalConsent && !vm.IsLegacyGlobalSelected, "Global mode/consent must never restore from disk");
            Require(errors.Messages.Count == 0, "Binding errors: " + string.Join("; ", errors.Messages));
            window.Close(); Pump();
            Console.WriteLine("PASS: WPF visual tree, mode/duration bindings, loss controls, shared settings, zero-duration, no duplicate pulse UI; no live driver or physical keyboard claim.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    // Driver-test child process, not another app/build. No WPF, hook or capture.
    // Loopback-only echo endpoint lets integration tests select a real OTHER PID.
    private static int RunUdpPeer(string[] args)
    {
        using var controlTcp = new System.Net.Sockets.TcpClient();
        if (args.Length == 4) controlTcp.Connect(System.Net.IPAddress.Loopback, int.Parse(args[3]));
        using var peer = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
        bool shared = args[2] == "shared";
        peer.ExclusiveAddressUse = !shared;
        if (shared) peer.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket,
            System.Net.Sockets.SocketOptionName.ReuseAddress, true);
        peer.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, int.Parse(args[1])));
        Console.WriteLine(((System.Net.IPEndPoint)peer.Client.LocalEndPoint!).Port);
        Console.Out.Flush();
        bool udpClosed = false;
        using var keepPidAlive = new ManualResetEventSlim();
        var commands = new Thread(() =>
        {
            while (Console.ReadLine() is { } line)
            {
                if (line != "close-udp") continue;
                Volatile.Write(ref udpClosed, true);
                peer.Dispose();
                Console.WriteLine("udp-closed"); Console.Out.Flush();
            }
        }) { IsBackground = true };
        commands.Start();
        while (true)
        {
            try
            {
                var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                byte[] bytes = peer.Receive(ref remote);
                peer.Send(bytes, bytes.Length, remote);
            }
            catch (Exception) when (Volatile.Read(ref udpClosed))
            {
                keepPidAlive.Wait(); // Keep the controlled PID/TCP connection alive after closing UDP.
                return 0;
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
