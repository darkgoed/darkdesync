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
    // Driver-free smoke also exercises the real Button's OnClick/command binding,
    // not ICommand.Execute. Native suite below additionally uses UIA client lookup.
    private static void InvokeButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        Pump();
    }

    private static int RunNativeUi()
    {
        if (Environment.GetEnvironmentVariable("DARKDESYNC_DRIVER_TESTS") != "1" ||
            Environment.GetEnvironmentVariable("DARKDESYNC_GLOBAL_TESTS") != "1")
            throw new InvalidOperationException("Native UI requires isolated-runner driver AND GLOBAL consent.");
        string dir = Path.Combine(Path.GetTempPath(), "DarkDesync-NativeUI-" + Guid.NewGuid());
        MainWindow? window = null;
        Process? child = null;
        try
        {
            var app = new App(); app.InitializeComponent();
            window = new MainWindow(new SettingsStore(Path.Combine(dir, "settings.json")));
            window.Show(); Pump();
            var vm = (MainViewModel)window.DataContext;
            var engine = (PacketEngine)typeof(MainViewModel).GetField("_packetEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            var controls = Descendants(window).ToArray();
            var effects = controls.OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, vm.DiagnosticModeOptions));
            var filters = controls.OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, vm.FilterModeOptions));
            var targets = controls.OfType<ComboBox>().Single(c => ReferenceEquals(c.ItemsSource, vm.Processes));
            var enabled = controls.OfType<CheckBox>().Single(c => Equals(c.Content, "Habilitado"));
            var status = (TextBlock)window.FindName("StartStatus");
            var start = (Button)window.FindName("StartStopButton");
            var pulse = controls.OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.TriggerCycleCommand));
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            void ClickStart()
            {
                // Genuine UI Automation client -> HWND tree -> InvokePattern -> WPF
                // Button.OnClick. Run on a separate MTA thread and keep STA pumping.
                var click = Task.Run(() =>
                {
                    var root = AutomationElement.FromHandle(hwnd);
                    var button = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "StartStopButton"));
                    Require(button != null && button.Current.IsEnabled, "UIA discovers enabled START/STOP");
                    ((InvokePattern)button!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                });
                Await(click); Pump();
            }
            void Select(DiagnosticMode mode, int filter)
            {
                effects.SelectedItem = ModeCatalog.Name(mode);
                filters.SelectedItem = vm.FilterModeOptions[filter]; Pump();
                Require(ModeCatalog.Parse(vm.SelectedDiagnosticModeText) == mode, "Ranked ComboBox enum identity");
                Require(vm.SelectedFilterModeText == vm.FilterModeOptions[filter], "Effect selection never changes explicit filter");
            }
            void Stopped()
            {
                Require(!engine.IsCaptureRequested && !engine.IsRunning && Equals(start.Content, "START"), "STOP/refusal must not fake arming");
            }
            vm.OnlyTargetForeground = false;
            Require(vm.DiagnosticCycleDurationMs == 200, "Ranking must not set 250ms");
            foreach (var mode in new[] { DiagnosticMode.DualTokenBucket, DiagnosticMode.FlowAwareShaping, DiagnosticMode.OutboundFreeze, DiagnosticMode.BandwidthShaping })
            {
                targets.SelectedItem = null; Select(mode, 4);
                enabled.IsChecked = false; Pump(); ClickStart();
                Require(engine.IsCaptureRequested && engine.IsRunning && Equals(start.Content, "STOP"), "Native GLOBAL START without PID/extra consent: " + mode);
                Require(status.Text.Contains("ARMADA") && status.Text.Contains("Habilitado"), "Disabled effect must not mask native arming");
                Require(!engine.IsEffectActive && engine.PacketsThrottled == 0, "START only arms");
                Await(TcpLoopbackProbe());
                InvokeButton(pulse); Require(!engine.IsEffectActive, "Habilitado=false refuses pulse, not START");
                enabled.IsChecked = true; vm.DiagnosticCycleDurationMs = 250; Pump();
                InvokeButton(pulse); Require(engine.IsEffectActive, "Manual WPF button pulses selected effect");
                Await(TcpLoopbackProbe());
                ClickStart(); Stopped();
                Require(engine.PacketsDropped == 0, "No legacy drop path");
                Console.WriteLine("PASS UIA/native GLOBAL: " + mode + "; no PID/extra consent; START no effect; checkbox; own TCP; pulse; STOP");
            }
            vm.DiagnosticCycleDurationMs = 200;
            foreach (var mode in new[] { DiagnosticMode.InboundBandwidthShaping, DiagnosticMode.CorrelatedInboundJitter, DiagnosticMode.AsymmetricDuplexShaping, DiagnosticMode.ControlledPacketReordering })
            {
                Select(mode, 4); ClickStart(); Stopped();
                Require(status.Text.Contains("INCOMPATÍVEL"), "GLOBAL refuses incompatible profile visibly");
            }
            Select(DiagnosticMode.PacketSizeAwareShaping, 4); ClickStart(); Stopped();
            Require(status.Text.Contains("Consentimento GLOBAL"), "GLOBAL size separate consent");
            var consent = controls.OfType<CheckBox>().Single(c => c.Content is string text && text.StartsWith("Consinto: GLOBAL"));
            consent.IsChecked = true; Pump(); ClickStart();
            Require(engine.IsRunning && !engine.IsEffectActive && vm.SelectedProcess == null, "GLOBAL size explicit opt-in starts without PID");
            ClickStart(); Stopped();

            // New child, no existing TCP tuple. Bind before FLOW observation; send
            // only after START so UDP creation is genuinely discovered by WinDivert.
            child = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { Assembly.GetExecutingAssembly().Location, "--udp-peer", "0", "exclusive" },
                RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true
            })!;
            string? line = Await(child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            int port = int.Parse(line!);
            var ownChild = new ProcessInfo { ProcessId = (uint)child.Id, ProcessName = "private-test-name" };
            vm.Processes.Add(ownChild);
            foreach (var mode in ModeCatalog.Choices.Values)
            {
                targets.SelectedItem = null; Select(mode, 0); ClickStart(); Stopped();
                if (PacketEngine.ProfileCompatibilityError(mode, FilterMode.Outbound) != null)
                {
                    Require(status.Text.Contains("INCOMPATÍVEL"), "Outbound incompatibility does not silently widen scope");
                    filters.SelectedItem = vm.FilterModeOptions[1]; Pump();
                }
                targets.SelectedItem = ownChild; Pump();
                Require(engine.IsObservingUdp && !engine.IsCaptureRequested, "Selection starts FLOW only");
                bool experimental = mode == DiagnosticMode.ControlledPacketReordering;
                if (experimental)
                {
                    ClickStart(); Stopped(); Require(status.Text.Contains("Opt-in"), "Explicit experimental refusal");
                    var optIn = controls.OfType<CheckBox>().Single(c => c.Content is string text && text.StartsWith("Opt-in EXPERIMENTAL"));
                    optIn.IsChecked = true; Pump();
                }
                ClickStart();
                Require(engine.IsCaptureRequested && !engine.IsRunning && Equals(start.Content, "STOP") && vm.StatusText == "AGUARDANDO", "START waits honestly for exact tuple: " + mode);
                ClickStart(); Stopped(); // STOP cancels wait through actual UIA.
                ClickStart();
                using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                client.Connect(IPAddress.Loopback, port);
                Await(client.SendAsync(new byte[] { 41 }, 1));
                Require(Await(client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer.SequenceEqual(new byte[] { 41 }), "Own child UDP reaches peer");
                Until(() => engine.IsRunning && engine.ScopeStatus.UdpScoped > 0);
                Require(status.Text.Contains("ARMADA"), "Live FLOW to NETWORK promotes high visible status");
                Require(engine.CurrentFilter.Contains("udp.SrcPort == " + port) && engine.CurrentFilter.Contains("udp.DstPort == " + port), "Exact child tuple, not just a broad port");
                Require(engine.ActiveDiagnosticMode == DiagnosticMode.None && engine.PacketsThrottled == 0, "No effect at START");
                InvokeButton(pulse); Require(engine.IsEffectActive, "Selected scoped effect activates only on pulse");
                // One pending packet; STOP drains it once, with no replay or drop.
                byte[] bytes = new byte[900]; bytes[0] = 42;
                Await(client.SendAsync(bytes, bytes.Length));
                Until(() => engine.PacketsThrottled > 0);
                ClickStart(); Stopped();
                Require(Await(client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer[0] == 42, "STOP drains own packet");
                Await(Task.Delay(50)); Require(client.Available == 0 && engine.PacketsDropped == 0, "No duplicate/replay/loss");
                Console.WriteLine("PASS UIA/native PID: " + mode + "; exact FLOW -> NETWORK; wait/STOP; pulse/drain; no duplicates");
            }
            // Inject only a native OPEN refusal, never a fabricated success.
            targets.SelectedItem = null; Select(DiagnosticMode.DualTokenBucket, 4);
            foreach (int code in new[] { 2, 5, 87, 577 })
            {
                engine.NetworkOpenFailureOverride = _ => code;
                ClickStart(); Stopped();
                Require(status.Text.Contains("Win32 " + code), "Native failure visible with exact code");
                Require(vm.SupportDiagnosticText.Contains("Win32=" + code), "Copy diagnostics retain native code");
            }
            engine.NetworkOpenFailureOverride = null;
            var copy = (Button)window.FindName("CopyDiagnosticButton");
            InvokeButton(copy);
            Require(Clipboard.GetText() == vm.SupportDiagnosticText, "Clipboard read-back matches explicitly clicked redacted diagnostic");
            Require(!Clipboard.GetText().Contains("private-test-name") && !Clipboard.GetText().Contains("127.0.0.1"), "Clipboard excludes identity/endpoints");
            Require(vm.Logs.Any(l => l.Contains("START PRESSED")) && vm.Logs.Any(l => l.Contains("START RESULT")), "Visible START attempt trail");
            Console.WriteLine("PASS native UI: real UIA InvokePattern START/STOP, ranked ComboBox bindings, all nine selections, Win32 failure injection, redacted Clipboard read-back. Physical keyboard and user's PC NOT tested; native non-loopback inbound NOT tested.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            window?.Close(); Pump();
            if (child != null) { if (!child.HasExited) child.Kill(entireProcessTree: true); child.WaitForExit(); child.Dispose(); }
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
