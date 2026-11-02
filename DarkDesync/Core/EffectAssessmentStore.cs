using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkDesync.Core;

public enum AssessmentCategory { Unrated, Legit, Balanced, Aggressive }
public sealed record EffectAssessment(DiagnosticMode Mode, int DurationMs, AssessmentCategory Category, string Note, int? PreferredDurationMs = null);
public sealed class EffectAssessmentStore
{
    private readonly IReadOnlyDictionary<(DiagnosticMode, int), EffectAssessment> _entries;
    private EffectAssessmentStore(IEnumerable<EffectAssessment> entries) => _entries = entries.ToDictionary(e => (e.Mode, e.DurationMs));
    public EffectAssessment? Find(DiagnosticMode mode, int durationMs) => _entries.GetValueOrDefault((mode, durationMs));
    public static EffectAssessmentStore Parse(string json)
    {
        if (json.Length > 65536) throw new InvalidDataException("Assessment file exceeds 64 KiB.");
        var entries = JsonSerializer.Deserialize<EffectAssessment[]>(json, new JsonSerializerOptions
        { Converters = { new JsonStringEnumConverter() } }) ?? throw new InvalidDataException("Empty assessments.");
        if (entries.Length > 256 || entries.Any(e => e == null || !Enum.IsDefined(e.Mode) || !Enum.IsDefined(e.Category) ||
            e.DurationMs is < 0 or > 800 || e.PreferredDurationMs is < 0 or > 800 || e.Note == null || e.Note.Length > 240 || e.Note.Any(char.IsControl)) ||
            entries.Select(e => (e.Mode, e.DurationMs)).Distinct().Count() != entries.Length)
            throw new InvalidDataException("Invalid/duplicate effect assessments.");
        return new EffectAssessmentStore(entries);
    }
    public static EffectAssessmentStore Load(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Assessment file exceeds 64 KiB.");
        return Parse(File.ReadAllText(path));
    }
    internal static EffectAssessmentStore LoadApplicationCatalog()
    {
        try { return Load(Path.Combine(AppContext.BaseDirectory, "effect-assessments.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        using var stream = typeof(EffectAssessmentStore).Assembly.GetManifestResourceStream("DarkDesync.effect-assessments.json")!;
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
