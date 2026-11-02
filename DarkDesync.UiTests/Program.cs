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
            Require(!vm.IsLegacyGlobalSelected && vm.EngineToggleText == "START", "Default is strict, START is idle");
            vm.SelectedFilterModeText = "Outbound legado — todos os aplicativos (global)"; Pump();
            Require(vm.SelectedDiagnosticModeText == "Atraso (ms)" && vm.GlobalDelayMs == 150,
                "Selecting GLOBAL defaults to finite packet delay, never implicit 100% packet loss");
            var warning = (TextBlock)window.FindName("GlobalLegacyWarning");
            Require(warning.IsVisible && warning.Text.Contains("TODAS") && warning.Text.Contains("Windows"), "GLOBAL scope must be explicit");
            Require(vm.EngineToggleText == "START", "Selecting GLOBAL does not open capture");
            Require(!controls.OfType<CheckBox>().Any(c => c.Name == "GlobalLegacyConsent"), "GLOBAL requires no extra consent checkbox");
            Require(controls.OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.ToggleEngineCommand) && b.IsEnabled),
                "START/STOP button is available in GLOBAL");
            vm.TriggerCycleCommand.Execute(null); Pump();
            Require(!vm.IsEngineRunning && vm.NetworkDiagnosticsText.Contains("GLOBAL"), "Manual pulse cannot arm GLOBAL without START");
            mode.SelectedItem = "Atraso (ms)"; Pump();
            Require(vm.IsGlobalDelayVisible, "Global latency delay slider must be visible");
            var delaySlider = controls.OfType<Slider>().Single(sl => sl.Minimum == 0 && sl.Maximum == 3000);
            Require(delaySlider.IsVisible, "Delay slider must be present and visible");
            delaySlider.Value = 450; Pump();
            Require(vm.GlobalDelayMs == 450, "Delay-ms slider must update the model");
            var pulseSlider = controls.OfType<Slider>().Single(sl => sl.Minimum == 0 && sl.Maximum == 800);
            Require(pulseSlider.IsVisible, "Pulse duration must also be visible in GLOBAL");
            pulseSlider.Value = 175; Pump();
            Require(vm.DiagnosticCycleDurationMs == 175 && vm.GlobalDelayMs == 450, "Pulse duration and per-packet delay are independent");
            var hotkeyHelp = (TextBlock)window.FindName("HotkeyHelp");
            Require(hotkeyHelp.Text.Contains("START") && hotkeyHelp.Text.Contains("Espaço"), "Help describes START then Space");
            Require(vm.NetworkDiagnosticsText.Contains("450 ms"), "Live diagnostics must show configured delay");
            vm.DiagnosticCycleDurationMs = 0; Pump();
            Require(vm.EngineToggleText == "START", "Zero-duration pulse does not restrict arming START");
            vm.SaveSettingsCommand.Execute(null); vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.IsLegacyGlobalSelected && vm.EngineToggleText == "START",
                "GLOBAL mode selection persists, capture never starts on load");
            Require(vm.GlobalDelayMs == 450 && vm.SelectedDiagnosticModeText == "Atraso (ms)",
                "Saved delay milliseconds/effect must roundtrip without starting capture");
            // Explicit driver-free lifecycle model; native capture is covered only by the Driver suite.
            var engine = (PacketEngine)typeof(MainViewModel).GetField("_packetEngine",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
            void ModelRunning()
            {
                typeof(PacketEngine).GetMethod("PrepareArmedCapture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(engine, null);
                typeof(PacketEngine).GetField("_isRunning", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(engine, true);
                typeof(PacketEngine).GetField("_captureRequested", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(engine, true);
                typeof(PacketEngine).GetField("_stopping", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(engine, false);
                typeof(MainViewModel).GetProperty(nameof(vm.IsEngineRunning))!.SetValue(vm, true);
            }
            var hotkey = (HotkeyService)typeof(MainViewModel).GetField("_hotkeyService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
            var dispatch = typeof(HotkeyService).GetMethod("DispatchKeyTransition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            void Key(bool down) => dispatch.Invoke(hotkey, new object[] { 0x20, down });
            vm.SelectedHotkeyName = "Espaço";
            Key(true); Pump(); Key(false);
            Require(!engine.IsCaptureRequested, "Space before START never captures");
            ModelRunning();
            Require(vm.EngineToggleText == "STOP", "Running engine exposes STOP");
            Require(engine.ActiveDiagnosticMode == DiagnosticMode.None, "START arms with no selected effect applied");
            vm.DiagnosticCycleDurationMs = 800;
            Key(true); Pump();
            Require(engine.IsEffectActive && engine.ActiveDiagnosticMode == DiagnosticMode.LatencyDelay, "Actual HotkeyPressed subscription starts selected pulse after START");
            vm.CancelCycleCommand.Execute(null); Pump();
            Require(engine.IsRunning && !engine.IsEffectActive, "Cancel restores without closing capture");
            Key(true); Pump();
            Require(!engine.IsEffectActive, "Held Space repeat cannot start another pulse even after cancellation");
            Key(false); Key(true); Pump(); Key(false);
            Require(engine.IsEffectActive, "Second physical press accepted after release");
            vm.CancelCycleCommand.Execute(null); Pump();
            vm.CycleProfileEnabled = false;
            vm.ThrottleEnabled = true; vm.ThrottleEnabled = false;
            vm.SelectedThrottleMode = vm.ThrottleModeOptions[1]; Pump();
            Require(engine.IsRunning && engine.StateMachine.Current == DesyncStateMachine.State.Armed,
                "Legacy controls cannot apply an effect to armed GLOBAL");
            mode.SelectedItem = "Drop (%)"; Pump();
            Require(engine.IsRunning && !engine.IsEffectActive && vm.EngineToggleText == "STOP" && vm.IsGlobalDropVisible,
                "Effect change preserves arming, never applies effect, and shows explicit Drop controls");
            Require(!delaySlider.IsVisible, "Delay widget hides for Drop");
            vm.DropPercentage = 37; vm.SaveSettingsCommand.Execute(null);
            vm.ResetSettingsCommand.Execute(null); vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.SelectedDiagnosticModeText == "Drop (%)" && vm.DropPercentage == 37 && !engine.IsRunning,
                "Explicit Drop roundtrip never starts capture");
            mode.SelectedItem = "Desativado"; vm.SaveSettingsCommand.Execute(null);
            vm.ResetSettingsCommand.Execute(null); vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.SelectedDiagnosticModeText == "Desativado" && !vm.IsGlobalDropVisible && !engine.IsRunning,
                "Old GLOBAL None settings remain disabled, never implicit loss");
            vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.SelectedDiagnosticModeText == "Desativado", "Repeated load is independent of previous mode");
            ModelRunning();
            vm.SelectedFilterModeText = vm.FilterModeOptions[0]; Pump();
            Require(!engine.IsRunning && vm.EngineToggleText == "START", "Filter change safely stops GLOBAL");
            vm.SelectedDiagnosticModeText = "Atraso (ms)"; Pump();
            Require(vm.IsDiagnosticModeSelected && delaySlider.IsVisible,
                "Strict Lag uses a diagnostic pulse, not implicit legacy drop");
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
