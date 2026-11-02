using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Automation;

internal static partial class Program
{
    // Self-automation can deadlock UIAutomationCore provider/client caches even
    // on separate apartments. Use an owned test-only process, not an agent or a
    // replacement app; it invokes the actual parent's accessible START control.
    private static Process StartExternalUiaClick(IntPtr hwnd)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { Assembly.GetExecutingAssembly().Location, "--uia-click", hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }
    private static int RunExternalUiaClick(string[] args)
    {
        if (!long.TryParse(args[1], CultureInfo.InvariantCulture, out long hwnd) || !int.TryParse(args[2], out int owner)) return 2;
        Exception? failure = null;
        var client = new Thread(() =>
        {
            try
            {
                Console.WriteLine("UIA external: FromHandle");
                var root = AutomationElement.FromHandle(new IntPtr(hwnd));
                Require(root.Current.ProcessId == owner, "UIA target is the owned test parent only");
                Console.WriteLine("UIA external: FindFirst");
                var button = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartStopButton"));
                Require(button != null && button.Current.IsEnabled, "Actual enabled START/STOP discovered");
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
