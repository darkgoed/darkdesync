using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Automation;

internal static partial class Program
{
    // Self-automation can deadlock UIAutomationCore provider/client caches even
    // on separate apartments. Use an owned test-only process, not an agent or a
    // replacement app; it invokes the actual parent's accessible START control.
    private static Process StartExternalUiaClick(IntPtr hwnd, System.Windows.Point point)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { Assembly.GetExecutingAssembly().Location, "--uia-click", hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), Environment.ProcessId.ToString(CultureInfo.InvariantCulture), ((int)Math.Round(point.X)).ToString(CultureInfo.InvariantCulture), ((int)Math.Round(point.Y)).ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }
    private static int RunExternalUiaClick(string[] args)
    {
        if (!long.TryParse(args[1], CultureInfo.InvariantCulture, out long hwnd) || !int.TryParse(args[2], out int owner) || !int.TryParse(args[3], out int x) || !int.TryParse(args[4], out int y)) return 2;
        Exception? failure = null;
        var client = new Thread(() =>
        {
            try
            {
                Console.WriteLine("UIA external: FromHandle");
                var root = AutomationElement.FromHandle(new IntPtr(hwnd));
                Require(root.Current.ProcessId == owner, "UIA target is the owned test parent only");
                // Whole-tree FindFirst traverses popup/item providers and can
                // block during a live GLOBAL pulse. Resolve the actual visible
                // point instead, then validate identity, owner and bounds.
                Console.WriteLine("UIA external: FromPoint");
                var point = new System.Windows.Point(x, y);
                var button = AutomationElement.FromPoint(point);
                for (int depth = 0; button != null && button.Current.AutomationId != "StartStopButton" && depth < 4; depth++)
                    button = TreeWalker.ControlViewWalker.GetParent(button);
                Require(button != null && button.Current.AutomationId == "StartStopButton" && button.Current.ProcessId == owner && button.Current.IsEnabled && !button.Current.IsOffscreen && button.Current.BoundingRectangle.Contains(point), "Actual visible owned START/STOP discovered at screen point");
                Console.WriteLine("UIA external: InvokePattern");
                ((InvokePattern)button!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                Console.WriteLine("PASS external UIA Invoke");
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        client.SetApartmentState(ApartmentState.MTA); client.Start();
        if (!client.Join(TimeSpan.FromSeconds(6))) { Console.Error.WriteLine("External UIA timeout (no direct-command fallback)"); return 1; }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }
}
