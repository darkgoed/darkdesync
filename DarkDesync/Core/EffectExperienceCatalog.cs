namespace DarkDesync.Core;

// Presentation metadata only. These are the owner's subjective observations at
// exact durations, not measurements or a claim about any game/anticheat.
public enum PulseStyle { Custom = 0, Legitimate = 1, Balanced = 2, Aggressive = 3 }
public sealed record EffectRating(string Label, string Background, string Foreground, string Note);
public static class EffectExperienceCatalog
{
    public static IReadOnlyDictionary<string, PulseStyle> Styles { get; } = new Dictionary<string, PulseStyle>
    {
        ["Personalizado"] = PulseStyle.Custom, ["Legítimo"] = PulseStyle.Legitimate,
        ["Equilibrado"] = PulseStyle.Balanced, ["Agressivo"] = PulseStyle.Aggressive
    };
    public static string StyleName(PulseStyle style) => Styles.FirstOrDefault(p => p.Value == style).Key ?? "Personalizado";
    public static int? FixedDuration(PulseStyle style) => style switch
    { PulseStyle.Legitimate => 250, PulseStyle.Aggressive => 800, _ => null };
    private static readonly EffectRating Untested = new("Não testado", "#374151", "#F9FAFB", "Sem avaliação pessoal fornecida para este efeito nesta duração.");
    private static readonly EffectRating Aggressive = new("Agressivo", "#7F1D1D", "#FEE2E2", "Avaliação pessoal fornecida a 800 ms; não é medida objetiva.");
    private static readonly EffectRating Balanced = new("Equilibrado", "#78350F", "#FEF3C7", "Avaliação pessoal fornecida a 800 ms; não é medida objetiva.");
    private static readonly IReadOnlyDictionary<(DiagnosticMode Mode, int Duration), EffectRating> Ratings =
        new Dictionary<(DiagnosticMode, int), EffectRating>
        {
            [(DiagnosticMode.DualTokenBucket, 800)] = Aggressive,
            [(DiagnosticMode.FlowAwareShaping, 800)] = Balanced,
            [(DiagnosticMode.OutboundFreeze, 800)] = Aggressive with { Note = "800 ms: injogável nos testes pessoais. 250 ms foi considerado o modo mais legítimo pelo usuário; seleção não é bloqueada." },
            [(DiagnosticMode.BandwidthShaping, 800)] = Balanced,
            [(DiagnosticMode.OutboundFreeze, 250)] = new("Legítimo", "#14532D", "#DCFCE7", "250 ms: considerado o modo mais legítimo pelo usuário nos testes pessoais; sem garantia de benefício em jogo.")
        };
    public static EffectRating Rating(DiagnosticMode mode, int durationMs) => Ratings.GetValueOrDefault((mode, durationMs), Untested);
}
