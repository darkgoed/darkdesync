namespace DarkDesync.Models;

/// <summary>
/// Information about a running process for the process selector UI.
/// </summary>
public class ProcessInfo
{
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public IntPtr MainWindowHandle { get; set; }

    /// <summary>Display string for the process selector.</summary>
    public string DisplayName =>
        string.IsNullOrEmpty(WindowTitle)
            ? $"{ProcessName} (PID: {ProcessId})"
            : $"{WindowTitle} — {ProcessName} (PID: {ProcessId})";
}
