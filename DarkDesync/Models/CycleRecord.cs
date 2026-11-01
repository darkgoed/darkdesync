namespace DarkDesync.Models;

/// <summary>
/// Records a completed desync cycle for the history log.
/// </summary>
public class CycleRecord
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public int DropPercentage { get; set; }
    public long PacketsDropped { get; set; }
    public long PacketsTotal { get; set; }
    public string TargetProcess { get; set; } = string.Empty;
    public bool CompletedNormally { get; set; }

    public string Summary =>
        $"[{StartTime:HH:mm:ss}] {ProfileName} — {DurationMs}ms — {PacketsDropped}/{PacketsTotal} packets dropped" +
        (CompletedNormally ? "" : " (interrupted)");
}
