using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows;
using DarkDesync;
using DarkDesync.Core;

internal static partial class Program
{
    private static int RunExternalLabUia(string[] args)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(new IntPtr(long.Parse(args[1], CultureInfo.InvariantCulture)));
                int owner = int.Parse(args[2], CultureInfo.InvariantCulture);
                Require(root.Current.ProcessId == owner, "Only owned laboratory window is automated");
                AutomationElement At(string id, int offset)
                {
                    Console.WriteLine("UIA lab point: " + id);
                    var point = new Point(int.Parse(args[offset], CultureInfo.InvariantCulture), int.Parse(args[offset + 1], CultureInfo.InvariantCulture));
                    var element = AutomationElement.FromPoint(point);
                    for (int depth = 0; element != null && element.Current.AutomationId != id && depth < 6; depth++)
                        element = TreeWalker.ControlViewWalker.GetParent(element);
                    Require(element != null && element.Current.AutomationId == id && element.Current.ProcessId == owner && element.Current.BoundingRectangle.Contains(point), "Visible owned lab control " + id);
                    return element!;
                }
                var selector = At("ExperimentSelector", 3);
                Console.WriteLine("UIA lab: expand");
                ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                Console.WriteLine("UIA lab: direct list children");
                var items = selector.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                Require(items.Count == 6, "Actual UIA lab exposes exactly six experiments");
                // Select NAT, which must be blocked without owned reflector consent.
                ((SelectionItemPattern)items[4].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
                ((InvokePattern)At("RunExperiment", 5).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                var value = (ValuePattern)At("ExperimentOutput", 7).GetCurrentPattern(ValuePattern.Pattern);
                // InvokePattern queues WPF work; wait for that one invocation's result,
                // not a retry of the experiment. JSON escapes must be decoded before assertions.
                var clock = Stopwatch.StartNew(); string text;
                do { text = value.Current.Value; if (text.StartsWith('{')) break; Thread.Sleep(10); } while (clock.Elapsed < TimeSpan.FromSeconds(3));
                Console.WriteLine("UIA lab evidence: " + text);
                using var json = JsonDocument.Parse(text);
                Require(json.RootElement.GetProperty("Status").GetString() == "Requer laboratório" &&
                    json.RootElement.GetProperty("Report").GetProperty("Id").GetInt32() == 4 &&
                    json.RootElement.GetProperty("Report").GetProperty("Evidence").GetProperty("Sent").GetInt32() == 0,
                    "Real UIA invocation runs NAT prerequisite diagnostic with zero sent packets");
                Console.WriteLine("PASS external UIA: separate six-item lab, selection + invoke, blocked NAT/no probes and STOP state");
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(12))) { Console.Error.WriteLine("UIA lab bounded timeout"); return 1; }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }

    private static void VerifyNetworkExperimentUi(MainWindow main, PacketEngine engine)
    {
        Require(ModeCatalog.OutboundChoices.Count == 4 && main.FindName("NetworkExperimentsButton") != null, "Four effects remain separate from lab launcher");
        var lab = new NetworkExperimentsWindow { Owner = main }; lab.Show(); Pump();
        try
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string arg in new[] { Assembly.GetExecutingAssembly().Location, "--uia-lab", new WindowInteropHelper(lab).Handle.ToInt64().ToString(CultureInfo.InvariantCulture), Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(arg);
            foreach (string name in new[] { "ExperimentSelector", "RunExperiment", "ExperimentOutput" })
            {
                var element = Descendants(lab).OfType<FrameworkElement>().Single(x => x.Name == name);
                var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
                info.ArgumentList.Add(((int)Math.Round(point.X)).ToString(CultureInfo.InvariantCulture));
                info.ArgumentList.Add(((int)Math.Round(point.Y)).ToString(CultureInfo.InvariantCulture));
            }
            using var child = Process.Start(info)!;
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            var clock = Stopwatch.StartNew();
            while (!child.HasExited && clock.Elapsed < TimeSpan.FromSeconds(18)) { Pump(); Thread.Sleep(10); }
            if (!child.HasExited) { child.Kill(true); throw new Exception("External lab UIA timeout"); }
            Console.WriteLine(output.GetAwaiter().GetResult());
            Require(child.ExitCode == 0, "Real external lab UIA: " + error.GetAwaiter().GetResult());
            Require(!Descendants(lab).OfType<System.Windows.Controls.Button>().Single(x => x.Name == "StopExperiment").IsEnabled, "STOP disabled after real completion");
            Require(!engine.IsCaptureRequested && !engine.IsEffectActive, "Laboratory never arms pulse engine");
        }
        finally { lab.Close(); Pump(); }
    }
}
