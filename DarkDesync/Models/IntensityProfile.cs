namespace DarkDesync.Models;

/// <summary>
/// Represents an intensity profile for packet loss simulation.
/// </summary>
public class IntensityProfile
{
    /// <summary>Display name (e.g., "Fraco", "Médio", "Forte").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Duration of the drop cycle in milliseconds.</summary>
    public int DurationMs { get; set; }

    /// <summary>Drop percentage (0-100).</summary>
    public int DropPercentage { get; set; } = 100;

    /// <summary>Color for UI display.</summary>
    public string Color { get; set; } = "#FFFFFF";

    public static IntensityProfile Weak => new()
    {
        Name = "Fraco",
        DurationMs = 800,
        DropPercentage = 100,
        Color = "#4CAF50" // Green
    };

    public static IntensityProfile Medium => new()
    {
        Name = "Médio",
        DurationMs = 2000,
        DropPercentage = 100,
        Color = "#FF9800" // Orange
    };

    public static IntensityProfile Strong => new()
    {
        Name = "Forte",
        DurationMs = 5000,
        DropPercentage = 100,
        Color = "#F44336" // Red
    };

    public static List<IntensityProfile> Defaults => [Weak, Medium, Strong];
}
