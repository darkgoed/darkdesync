using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkDesync.Core;

namespace DarkDesync.Services;
public enum ThrottleMode { Hotkey, Continuous }
public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public bool CycleProfileEnabled { get; init; } = true;
    public bool ThrottleEnabled { get; init; }
    public ThrottleMode ThrottleMode { get; init; }
    public int DurationMs { get; init; } = 800;
    public int DropPercentage { get; init; } = 100;
    public int RetentionMs { get; init; } = 1000;
    public int Chance { get; init; } = 100;
    public int SpacingMs { get; init; }
    public int JitterMs { get; init; }
    public string Hotkey { get; init; } = "Espaço";
    public bool OnlyTargetForeground { get; init; } = true;
    public FilterMode FilterMode { get; init; } = FilterMode.Outbound;
    public string CustomFilter { get; init; } = "outbound";
    public int DiagnosticDurationMs { get; init; } = 150;
    public int BurstLossPercentage { get; init; } = 50;
    public int AsymmetricInboundPercentage { get; init; } = 25;
    public int AsymmetricOutboundPercentage { get; init; } = 75;
    public DiagnosticMode DiagnosticMode { get; init; } = DiagnosticMode.None;
    public AppSettings Validate() => this with
    {
        DurationMs = Math.Clamp(DurationMs, 50, 10000), DropPercentage = Math.Clamp(DropPercentage, 0, 100),
        RetentionMs = Math.Clamp(RetentionMs, 10, 3000), Chance = Math.Clamp(Chance, 0, 100),
        SpacingMs = Math.Clamp(SpacingMs, 0, 200), JitterMs = Math.Clamp(JitterMs, 0, 2000),
        ThrottleMode = Enum.IsDefined(ThrottleMode) ? ThrottleMode : ThrottleMode.Hotkey,
        FilterMode = Enum.IsDefined(FilterMode) ? FilterMode : FilterMode.Outbound,
        Hotkey = Hotkey != null && HotkeyService.GetAvailableKeys().ContainsKey(Hotkey) ? Hotkey : "Espaço",
        CustomFilter = string.IsNullOrWhiteSpace(CustomFilter) ? "outbound" : CustomFilter,
        DiagnosticDurationMs = Math.Clamp(DiagnosticDurationMs, 0, 800),
        BurstLossPercentage = Math.Clamp(BurstLossPercentage, 0, 100),
        AsymmetricInboundPercentage = Math.Clamp(AsymmetricInboundPercentage, 0, 100),
        AsymmetricOutboundPercentage = Math.Clamp(AsymmetricOutboundPercentage, 0, 100),
        DiagnosticMode = Enum.IsDefined(DiagnosticMode) ? DiagnosticMode : DiagnosticMode.None
    };
}
public sealed class SettingsStore
{
    public string FilePath { get; }
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DarkDesync", "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public SettingsStore(string? filePath = null) => FilePath = filePath ?? DefaultPath;
    public AppSettings Load()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? throw new InvalidDataException("JSON vazio.");
        if (settings.Version != 1) throw new InvalidDataException("Versão de configuração não suportada.");
        return settings.Validate();
    }
    public void Save(AppSettings settings)
    {
        string full = Path.GetFullPath(FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings.Validate(), Options); stream.Flush(true);
            }
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
