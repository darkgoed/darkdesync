using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using DarkDesync;

internal static partial class Program
{
    private static int RunExternalPanelUia(string[] args)
    {
        Exception? failure = null;
        var client = new Thread(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(new IntPtr(long.Parse(args[1], CultureInfo.InvariantCulture)));
                int owner = int.Parse(args[2], CultureInfo.InvariantCulture);
                Require(root.Current.ProcessId == owner, "Only the owned test panel is automated");
                var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement button in buttons)
                    Require(button.Current.AutomationId != "NetworkExperimentsButton" && !button.Current.Name.Contains("Experimentos de rede"), "Removed laboratory launcher absent from actual UIA tree");
                var point = new Point(int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture));
                var selector = AutomationElement.FromPoint(point);
                for (int depth = 0; selector != null && selector.Current.ControlType != ControlType.ComboBox && depth < 6; depth++)
                    selector = TreeWalker.ControlViewWalker.GetParent(selector);
                Require(selector != null && selector.Current.ProcessId == owner && selector.Current.BoundingRectangle.Contains(point), "Actual visible owned effect selector");
                var expand = (ExpandCollapsePattern)selector!.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                expand.Expand();
                try
                {
                    var items = selector.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                    string[] expected = { "1º Dual Token Bucket", "2º Flow-Aware Shaping", "3º Outbound Freeze", "Bandwidth Shaping" };
                    Require(items.Count == expected.Length, "Actual UIA selector exposes exactly four favorites");
                    for (int i = 0; i < items.Count; i++)
                    {
                        Require(items[i].Current.Name.Contains(expected[i]), "Approved effect identity: " + expected[i]);
                        ((SelectionItemPattern)items[i].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                    }
                    // Restore the initial production default after exercising each real item.
                    ((SelectionItemPattern)items[2].GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                }
                finally { expand.Collapse(); }
                Console.WriteLine("PASS external panel UIA: exactly four approved effects selected; six-experiment launcher absent");
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        client.SetApartmentState(ApartmentState.MTA); client.Start();
        if (!client.Join(TimeSpan.FromSeconds(12))) { Console.Error.WriteLine("External panel UIA bounded timeout"); return 1; }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }

    private static void VerifyCleanPanelUia(MainWindow window, ComboBox effects)
    {
        Require(window.FindName("NetworkExperimentsButton") == null, "Removed laboratory has no WPF launcher");
        Require(typeof(MainWindow).Assembly.GetType("DarkDesync.NetworkExperimentsWindow") == null, "Removed window is not compiled into product");
        var point = effects.PointToScreen(new Point(effects.ActualWidth / 2, effects.ActualHeight / 2));
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { Assembly.GetExecutingAssembly().Location, "--uia-panel", new WindowInteropHelper(window).Handle.ToInt64().ToString(CultureInfo.InvariantCulture), Environment.ProcessId.ToString(CultureInfo.InvariantCulture), ((int)Math.Round(point.X)).ToString(CultureInfo.InvariantCulture), ((int)Math.Round(point.Y)).ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(argument);
        using var child = Process.Start(info)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        var clock = Stopwatch.StartNew();
        while (!child.HasExited && clock.Elapsed < TimeSpan.FromSeconds(18)) { Pump(); Thread.Sleep(10); }
        if (!child.HasExited) { child.Kill(true); throw new TimeoutException("External panel UIA timeout"); }
        Console.WriteLine(output.GetAwaiter().GetResult());
        Require(child.ExitCode == 0, "External panel UIA: " + error.GetAwaiter().GetResult());
        Pump();
    }
}
