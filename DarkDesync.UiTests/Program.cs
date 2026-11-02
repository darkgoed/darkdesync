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
internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if ((args.Length == 3 || args.Length == 4) && args[0] == "--udp-peer") return RunUdpPeer(args);
        if (args.SequenceEqual(new[] { "--driver-ui" })) return RunNativeUi();
        string temporary = Path.Combine(Path.GetTempPath(), "DarkDesync-UI-" + Guid.NewGuid());
        var errors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            var app = CreateTestApplication();
            var window = new MainWindow(new SettingsStore(Path.Combine(temporary, "settings.json")));
            var vm = (MainViewModel)window.DataContext;
            window.Show(); Pump();
            Require(vm.SelectedHotkeyName == "Espaço", "Default global hotkey");
            Require(!vm.IsEngineRunning, "Opening UI must not start capture");
            var diagnostics = (TextBlock)window.FindName("NetworkScopeDiagnostics");
            Require(diagnostics.Text.Contains("TCP:") && diagnostics.Text.Contains("UDP:"), "Protocol diagnosis binding");
            Require(diagnostics.Text.Length > 0, "Useful capture diagnostics retained");
            var controls = Descendants(window).ToArray();
            var mode = controls.OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, vm.DiagnosticModeOptions));
            Require(controls.OfType<ComboBox>().Count(c => ReferenceEquals(c.ItemsSource, vm.AvailableHotkeys.Keys)) <= 1, "One hotkey picker");
            Require(vm.DiagnosticModeOptions.SequenceEqual(new[] { "1º Dual Token Bucket", "2º Flow-Aware Shaping", "3º Outbound Freeze", "Bandwidth Shaping", "Inbound Bandwidth Shaping", "Correlated Inbound Jitter", "Packet Size-Aware Shaping", "Asymmetric Duplex Shaping", "Controlled Packet Reordering — EXPERIMENTAL" }), "Personal ranking then six unchanged unranked choices");
            Require(vm.SelectedDiagnosticModeText == ModeCatalog.Name(DiagnosticMode.OutboundFreeze) && Equals(mode.SelectedItem, vm.SelectedDiagnosticModeText) && vm.DiagnosticCycleDurationMs == 200, "Freeze / 200ms defaults, reordering does not select first item");
            Require(controls.OfType<TextBlock>().Any(t => t.Text == "Ranking pessoal · avaliação a 250 ms"), "Ranking context is subjective, not applied duration");
            Require(controls.OfType<Slider>().Count() == 1, "Only shared pulse slider; retention/loss controls removed");
            Require(!controls.OfType<TextBlock>().Any(t => t.Text.Contains("RETENÇÃO E JITTER")), "Retention panel removed");
            foreach (string effect in vm.DiagnosticModeOptions)
            {
                mode.SelectedItem = effect; Pump();
                Require(vm.SelectedDiagnosticModeText == effect, "Mode selection binding: " + effect);
                Require(vm.IsDiagnosticModeSelected && !vm.IsLegacyModeSelected, "Unified profile visibility");
                var duration = controls.OfType<Slider>().Single(s => s.Maximum == 800 && s.Minimum == 0);
                duration.Value = 175; Pump();
                Require(vm.DiagnosticCycleDurationMs == 175, "Duration slider binding");
                Require(duration.IsVisible, "Diagnostic duration visible inside profile");

            }
            var optIn = controls.OfType<CheckBox>().Single(c => c.Content is string text && text.StartsWith("Opt-in EXPERIMENTAL"));
            Require(optIn.IsVisible && !vm.ExperimentalOptIn, "Experimental opt-in visible and default off");
            optIn.IsChecked = true; Pump(); Require(vm.ExperimentalOptIn, "Consent binding");
            vm.BurstLossPercentage = 37; vm.AsymmetricInboundLossPercentage = 12; vm.AsymmetricOutboundLossPercentage = 84;
            vm.SelectedHotkeyName = "F2"; vm.DiagnosticCycleDurationMs = 800;
            vm.SaveSettingsCommand.Execute(null);
            vm.ResetSettingsCommand.Execute(null); Pump();
            Require(vm.DiagnosticCycleDurationMs == 200, "Default 200 ms");
            vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.DiagnosticCycleDurationMs == 800 && vm.IsExperimentalSelected && !vm.ExperimentalOptIn, "Experimental mode roundtrip WITHOUT consent");
            Require(vm.BurstLossPercentage == 37 && vm.AsymmetricInboundLossPercentage == 12 && vm.AsymmetricOutboundLossPercentage == 84, "Loss percentages roundtrip");
            Require(vm.SelectedHotkeyName == "F2", "Shared hotkey roundtrip");
            vm.DiagnosticCycleDurationMs = 0; vm.TriggerCycleCommand.Execute(null); Pump();
            Require(!vm.IsEngineRunning, "Zero-duration pulse must not start capture");
            Require(controls.OfType<TextBox>().All(t => !t.Text.Contains("127.0.0.1")), "No manual endpoint input");
            Require(controls.OfType<Button>().Count(b => ReferenceEquals(b.Command, vm.TriggerCycleCommand)) == 1, "Exactly one pulse button");
            vm.ResetSettingsCommand.Execute(null); Pump();
            Require(!vm.IsLegacyGlobalSelected && vm.EngineToggleText == "START", "Default is strict, START is idle");
            vm.SelectedFilterModeText = "Outbound legado — todos os aplicativos (global)"; Pump();
            Require(vm.SelectedDiagnosticModeText == ModeCatalog.Name(DiagnosticMode.OutboundFreeze), "GLOBAL preserves safe Freeze default");
            Require(vm.SelectedFilterModeText.Contains("todos os aplicativos"), "GLOBAL scope explicit in filter option");
            Require(vm.EngineToggleText == "START", "Selecting GLOBAL does not open capture");
            Require(!controls.OfType<CheckBox>().Any(c => c.Name == "GlobalLegacyConsent"), "GLOBAL requires no extra consent checkbox");
            Require(controls.OfType<Button>().Any(b => ReferenceEquals(b.Command, vm.ToggleEngineCommand) && b.IsEnabled),
                "START/STOP button is available in GLOBAL");
            vm.TriggerCycleCommand.Execute(null); Pump();
            Require(!vm.IsEngineRunning && vm.NetworkDiagnosticsText.Contains("GLOBAL"), "Manual pulse cannot arm GLOBAL without START");
            mode.SelectedItem = ModeCatalog.Name(DiagnosticMode.DualTokenBucket); Pump();
            var pulseSlider = controls.OfType<Slider>().Single(sl => sl.Minimum == 0 && sl.Maximum == 800);
            Require(pulseSlider.IsVisible, "Pulse duration must also be visible in GLOBAL");
            pulseSlider.Value = 175; Pump();
            Require(vm.DiagnosticCycleDurationMs == 175, "One pulse duration for GLOBAL too");
            Require(window.FindName("HotkeyHelp") == null && window.FindName("GlobalLegacyWarning") == null, "Redundant instruction blocks removed");
            vm.DiagnosticCycleDurationMs = 0; Pump();
            Require(vm.EngineToggleText == "START", "Zero-duration pulse does not restrict arming START");
            vm.SaveSettingsCommand.Execute(null); vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.IsLegacyGlobalSelected && vm.EngineToggleText == "START",
                "GLOBAL mode selection persists, capture never starts on load");
            Require(vm.SelectedDiagnosticModeText == ModeCatalog.Name(DiagnosticMode.DualTokenBucket), "New effect roundtrip without starting capture");
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
            vm.DiagnosticCycleDurationMs = 200;
            Key(true); Pump();
            Require(engine.IsEffectActive && engine.ActiveDiagnosticMode == DiagnosticMode.DualTokenBucket, "Actual HotkeyPressed subscription starts selected 200ms pulse after START");
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
            mode.SelectedItem = "Bandwidth Shaping"; Pump();
            Require(engine.IsRunning && !engine.IsEffectActive && vm.EngineToggleText == "STOP", "Effect change preserves arming without effect");
            vm.DropPercentage = 37; vm.SaveSettingsCommand.Execute(null);
            vm.ResetSettingsCommand.Execute(null); vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.SelectedDiagnosticModeText == "Bandwidth Shaping" && vm.DropPercentage == 37 && !engine.IsRunning,
                "Hidden legacy values survive save without being selected as effect");
            vm.SaveSettingsCommand.Execute(null);
            vm.ResetSettingsCommand.Execute(null); vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.SelectedDiagnosticModeText == "Bandwidth Shaping" && !engine.IsRunning, "Loading never starts capture");
            vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.SelectedDiagnosticModeText == "Bandwidth Shaping", "Repeated load is independent of previous mode");
            ModelRunning();
            vm.SelectedFilterModeText = vm.FilterModeOptions[0]; Pump();
            Require(!engine.IsRunning && vm.EngineToggleText == "START", "Filter change safely stops GLOBAL");
            vm.SelectedDiagnosticModeText = "Dual Token Bucket"; Pump();
            Require(vm.IsDiagnosticModeSelected && pulseSlider.IsVisible, "Strict effects share the pulse control");
            ModelRunning();
            mode.SelectedItem = "Asymmetric Duplex Shaping"; Pump();
            Require(!engine.IsRunning && vm.EngineToggleText == "START", "New direction scope requires a fresh START");
            Require(!optIn.IsVisible && !vm.ExperimentalOptIn, "Consent hidden and revoked outside experimental");
            var startStatus = (TextBlock)window.FindName("StartStatus");
            foreach (string effect in vm.DiagnosticModeOptions.Skip(4))
            {
                vm.SelectedProcess = null;
                vm.SelectedFilterModeText = vm.FilterModeOptions[4];
                mode.SelectedItem = effect; Pump();
                vm.ToggleEngineCommand.Execute(null); Pump();
                Require(startStatus.IsVisible && startStatus.Text == vm.StartStatusText && startStatus.Text.Contains(effect == "Packet Size-Aware Shaping" ? "Consentimento GLOBAL" : "INCOMPATÍVEL"),
                    "Always-visible START preflight, outside closed diagnostic expander: " + effect);
                Require(vm.EngineToggleText == "START" && !engine.IsCaptureRequested, "Missing PID never opens GLOBAL: " + effect);
            }
            // Driver-free controls exercise pending START/STOP with a real live PID,
            // but no fabricated native success. Real FLOW -> NETWORK is in Driver tests.
            engine.UdpFlowsOverride = () => []; engine.EndpointsOverride = _ => [];
            var targetPicker = controls.OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, vm.Processes));
            var enabled = controls.OfType<CheckBox>().Single(c => Equals(c.Content, "Habilitado"));
            var startButton = controls.OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.ToggleEngineCommand));
            var own = new DarkDesync.Models.ProcessInfo { ProcessId = (uint)Environment.ProcessId, ProcessName = "UI-owned PID" };
            vm.Processes.Add(own); targetPicker.SelectedItem = own; Pump();
            Require(vm.SelectedProcess == own && vm.SelectedFilterModeText == vm.FilterModeOptions[4], "PID binding preserves GLOBAL selection");
            vm.SelectedFilterModeText = vm.FilterModeOptions[0]; Pump();
            // Earlier settings roundtrip intentionally persisted Habilitado=false.
            enabled.IsChecked = true; Pump();
            Require(vm.CycleProfileEnabled, "Enable the profile before the pending START scenario");
            Require(optIn.IsVisible, "Reordering opt-in is visible");
            InvokeButton(startButton); Pump();
            Require(startStatus.Text.Contains("Opt-in") && !engine.IsCaptureRequested, "Opt-in preflight uses visible status");
            optIn.IsChecked = true; Pump();
            InvokeButton(startButton); Pump();
            Require(Equals(startButton.Content, "STOP") && vm.StatusText == "AGUARDANDO" && startStatus.Text.Contains("Aguardando conexões"),
                $"START button binding reflects pending capture, not active: button={startButton.Content}, status={vm.StatusText}, detail={startStatus.Text}");
            Require(vm.SelectedFilterModeText == vm.FilterModeOptions[0], "START keeps PID Outbound selection");
            enabled.IsChecked = false; Pump();
            Require(!vm.CycleProfileEnabled && startStatus.Text.Contains("Habilitado"), "Enable checkbox binding gives explicit instruction");
            enabled.IsChecked = true; Pump();
            InvokeButton(startButton); Pump();
            Require(Equals(startButton.Content, "START") && !engine.IsCaptureRequested, "STOP cancels pending START through UI command");
            vm.SelectedFilterModeText = vm.FilterModeOptions[4];
            mode.SelectedItem = "Packet Size-Aware Shaping"; Pump();
            var globalConsent = controls.OfType<CheckBox>().Single(c => c.Content is string text && text.StartsWith("Consinto: GLOBAL"));
            Require(globalConsent.IsVisible && !vm.GlobalPacketSizeConsent, "Separate visible GLOBAL consent defaults off");
            globalConsent.IsChecked = true; Pump(); Require(vm.GlobalPacketSizeConsent, "GLOBAL consent binding");
            vm.SaveSettingsCommand.Execute(null); vm.LoadSettingsCommand.Execute(null); Pump();
            Require(vm.IsGlobalPacketSizeSelected && !vm.GlobalPacketSizeConsent && !engine.IsCaptureRequested, "GLOBAL size settings preserve selection but never consent or capture");
            Require(errors.Messages.Count == 0, "Binding errors: " + string.Join("; ", errors.Messages));
            window.Close(); Pump();
            Console.WriteLine("PASS: nine choices, minimal controls, experimental opt-in, 200ms default, WPF bindings, settings, START/Space/STOP lifecycle; no physical keyboard/game claim.");
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
