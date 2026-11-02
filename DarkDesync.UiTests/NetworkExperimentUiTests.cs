using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Automation;
using System.Windows.Interop;
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
                AutomationElement Find(string id) => root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ?? throw new Exception("Missing UIA " + id);
                var selector = Find("ExperimentSelector");
                ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
                var items = selector.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                Require(items.Count == 6, "Actual UIA lab exposes exactly six experiments");
                // Select NAT, which must be blocked without owned reflector consent.
                ((SelectionItemPattern)items[4].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                ((ExpandCollapsePattern)selector.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();
                ((InvokePattern)Find("RunExperiment").GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                string text = ((ValuePattern)Find("ExperimentOutput").GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                Require(text.Contains("Requer laboratório") && text.Contains("Sent"), "Real UIA invocation runs NAT prerequisite diagnostic with zero sent packets");
                Require(!Find("StopExperiment").Current.IsEnabled, "STOP disabled after completion");
                Console.WriteLine("PASS external UIA: separate six-item lab, selection + invoke, blocked NAT/no probes and STOP state");
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(12))) return 1;
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
            using var child = Process.Start(info)!;
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            var clock = Stopwatch.StartNew();
            while (!child.HasExited && clock.Elapsed < TimeSpan.FromSeconds(18)) { Pump(); Thread.Sleep(10); }
            if (!child.HasExited) { child.Kill(true); throw new Exception("External lab UIA timeout"); }
            Console.WriteLine(output.GetAwaiter().GetResult());
            Require(child.ExitCode == 0, "Real external lab UIA: " + error.GetAwaiter().GetResult());
            Require(!engine.IsCaptureRequested && !engine.IsEffectActive, "Laboratory never arms pulse engine");
        }
        finally { lab.Close(); Pump(); }
    }
}
