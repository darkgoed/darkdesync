namespace DarkDesync.Core;

// Balanced is a legacy serialized identity only, never a third primary preset.
public enum PulseStyle { Custom = 0, Legitimate = 1, Balanced = 2, Aggressive = 3 }
public sealed record EffectRating(string Label, string Background, string Foreground, string Note);
public static class EffectExperienceCatalog
{
    public static IReadOnlyDictionary<string, PulseStyle> Styles { get; } = new Dictionary<string, PulseStyle>
    { ["Teste manual"] = PulseStyle.Custom, ["Legit"] = PulseStyle.Legitimate, ["Agressivo"] = PulseStyle.Aggressive };
    public static IReadOnlyList<string> PrimaryStyles { get; } = new[] { "Legit", "Agressivo" };
    public static string StyleName(PulseStyle style) => Styles.FirstOrDefault(p => p.Value == style).Key ?? "Teste manual";
    public static int? FixedDuration(PulseStyle style) => style switch
    { PulseStyle.Legitimate => 250, PulseStyle.Aggressive => 800, _ => null };
    private static readonly Lazy<EffectAssessmentStore> Assessments = new(EffectAssessmentStore.LoadApplicationCatalog);
    public static EffectRating Rating(DiagnosticMode mode, int durationMs)
    {
        var entry = Assessments.Value.Find(mode, durationMs);
        return (entry?.Category ?? AssessmentCategory.Unrated) switch
        {
            AssessmentCategory.Legit => new("Legit", "#14532D", "#DCFCE7", entry!.Note),
            AssessmentCategory.Balanced => new("Equilibrado", "#78350F", "#FEF3C7", entry!.Note),
            AssessmentCategory.Aggressive => new("Agressivo", "#7F1D1D", "#FEE2E2", entry!.Note),
            _ => new("Não avaliado", "#374151", "#F9FAFB", "Sem avaliação pessoal nesta duração.")
        };
    }
}
