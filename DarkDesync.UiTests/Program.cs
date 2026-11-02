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
            var store = new SettingsStore(Path.Combine(temporary, "settings.json"));
            var window = new MainWindow(store); app.MainWindow = window;
            var vm = (MainViewModel)window.DataContext;
            window.Show(); Pump();
            var engine = (PacketEngine)typeof(MainViewModel).GetField("_packetEngine",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(vm)!;
            var controls = Descendants(window).ToArray();
            var mode = controls.OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, vm.DiagnosticModeOptions));
            var startButton = (Button)window.FindName("StartStopButton");
            var state = (TextBlock)window.FindName("CaptureState");
            var status = (TextBlock)window.FindName("StartStatus");
            var enabled = controls.OfType<CheckBox>().Single(c => Equals(c.Content, "Habilitado"));
            var consent = (CheckBox)window.FindName("PacketSizeConsent");
            Require(controls.OfType<ComboBox>().Count() == 2, "Only effect and hotkey selectors; no process/network/protocol picker");
            Require(((TextBlock)window.FindName("OutboundScope")).Text == "Outbound · todos os aplicativos", "Fixed global scope warning");
            Require(!controls.OfType<CheckBox>().Any(c => c.Content is string t && (t.Contains("alvo") || t.Contains("EXPERIMENTAL"))), "No target/experimental controls");
            Require(vm.DiagnosticModeOptions.SequenceEqual(new[] { "1º Dual Token Bucket", "2º Flow-Aware Shaping", "3º Outbound Freeze", "Bandwidth Shaping", "Packet Size-Aware Shaping" }), "Five genuine outbound effects, personal ranking intact");
            Require(vm.SelectedDiagnosticModeText == ModeCatalog.Name(DiagnosticMode.OutboundFreeze) && Equals(mode.SelectedItem, vm.SelectedDiagnosticModeText), "Reorder must not pick rank 1 by accident");
            Require(vm.DiagnosticCycleDurationMs == 200 && vm.SelectedHotkeyName == "Espaço", "Default pulse/hotkey preserved");
            Require(vm.IsLegacyGlobalSelected && vm.SelectedProcess == null && !engine.IsCaptureRequested && state.Text == "PARADO / PAUSADO", "Default global is prepared, not auto-started");
            Require(state.IsVisible && state.FontSize >= 22 && status.IsVisible, "Conspicuous capture state independent of pulse");
            Require(!vm.NetworkDiagnosticsText.Contains("TCP") && !vm.NetworkDiagnosticsText.Contains("UDP") && !vm.StartStatusText.Contains("PID"), "No protocol/PID prerequisites in the main flow");
            foreach (string effect in vm.DiagnosticModeOptions)
            {
                mode.SelectedItem = effect; Pump();
                Require(vm.SelectedDiagnosticModeText == effect && vm.IsLegacyGlobalSelected && vm.SelectedProcess == null, "Effect binding preserves fixed scope");
                var duration = controls.OfType<Slider>().Single(); duration.Value = 175; Pump();
                Require(vm.DiagnosticCycleDurationMs == 175, "Ranking does not override the duration");
            }
            Require(consent.IsVisible && !consent.IsChecked.GetValueOrDefault(), "Packet Size has separate default-off consent");
            InvokeButton(startButton);
            Require(status.Text.Contains("Consentimento") && !engine.IsCaptureRequested, "Missing consent refuses visibly, without false ACTIVE");
            consent.IsChecked = true; Pump(); vm.SaveSettingsCommand.Execute(null); vm.LoadSettings(); Pump();
            Require(!vm.GlobalPacketSizeConsent && !engine.IsCaptureRequested, "Consent never persists or auto-starts");
            foreach (var oldFilter in Enum.GetValues<FilterMode>())
            {
                store.Save(new AppSettings { FilterMode = oldFilter, DiagnosticMode = DiagnosticMode.FlowAwareShaping, DiagnosticDurationMs = 175, OnlyTargetForeground = true });
                byte[] before = File.ReadAllBytes(store.FilePath); vm.LoadSettings(); Pump();
                Require(vm.IsLegacyGlobalSelected && vm.SelectedProcess == null && !vm.OnlyTargetForeground, "Old scoped config migrates in memory to fixed outbound without a target");
                Require(vm.DiagnosticCycleDurationMs == 175 && Equals(mode.SelectedItem, ModeCatalog.Name(DiagnosticMode.FlowAwareShaping)), "Loaded choice and duration preserved");
                Require(before.SequenceEqual(File.ReadAllBytes(store.FilePath)) && !engine.IsCaptureRequested, "Migration is read-only; no trigger");
            }
            foreach (var hidden in ModeCatalog.Choices.Values.Except(ModeCatalog.OutboundChoices.Values))
            {
                store.Save(new AppSettings { DiagnosticMode = hidden, FilterMode = FilterMode.AllTraffic, DiagnosticDurationMs = 250 });
                byte[] before = File.ReadAllBytes(store.FilePath); vm.LoadSettings(); Pump();
                Require(mode.SelectedItem == null && vm.SettingsStatus.Contains("indisponível"), "Hidden saved effect is explicitly incompatible, never silently substituted");
                InvokeButton(startButton);
                Require(status.Text.Contains("INCOMPATÍVEL") && state.Text == "PARADO / PAUSADO" && !engine.IsCaptureRequested, "Hidden mode cannot arm global");
                Require(before.SequenceEqual(File.ReadAllBytes(store.FilePath)), "Refusal does not rewrite old settings");
                vm.SaveSettingsCommand.Execute(null); Require(store.Load().DiagnosticMode == hidden, "Explicit save retains unavailable enum until user chooses a replacement");
                mode.SelectedItem = ModeCatalog.Name(DiagnosticMode.DualTokenBucket); Pump();
                Require(vm.SelectedDiagnosticModeText == ModeCatalog.Name(DiagnosticMode.DualTokenBucket), "User can explicitly choose supported replacement");
            }
            enabled.IsChecked = true; Pump();
            foreach (int code in new[] { 2, 5, 87, 577 })
            {
                engine.NetworkOpenFailureOverride = filter => { Require(filter == "outbound", "Only literal global outbound reaches native OPEN"); return code; };
                InvokeButton(startButton);
                Require(!engine.IsCaptureRequested && !engine.IsRunning && Equals(startButton.Content, "START") && state.Text == "PARADO / PAUSADO", "Driver refusal never claims ACTIVE");
                Require(status.Text.Contains("Win32 " + code) && status.Text.Contains("Copiar diagnóstico"), "Native code and concrete support instructions");
            }
            engine.NetworkOpenFailureOverride = null;
            InvokeButton((Button)window.FindName("CopyDiagnosticButton"));
            Require(Clipboard.GetText() == vm.SupportDiagnosticText && !Clipboard.GetText().Contains("PID") && !Clipboard.GetText().Contains("FLOW"), "Explicit Clipboard read-back without target prerequisites");
            Require(errors.Messages.Count == 0, "Binding errors: " + string.Join("; ", errors.Messages));
            window.Close(); Pump();
            Console.WriteLine("PASS outbound-only WPF: five ranked choices, no network/process/protocol selectors, conspicuous state, old scopes migrated read-only, hidden enums retained/refused, Win32 failures, consent and Clipboard. No fabricated native success; real arming is tested by --driver-ui.");
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
