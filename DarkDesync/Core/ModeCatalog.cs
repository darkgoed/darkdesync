namespace DarkDesync.Core;

public static class ModeCatalog
{
    // Personal evaluation at 250 ms, NOT a technical claim or the applied duration.
    // Reorder this list only; labels, ComboBox order and parser derive from it.
    public static IReadOnlyList<DiagnosticMode> PersonalRanking250ms { get; } = Array.AsReadOnly(new[]
    {
        DiagnosticMode.DualTokenBucket, DiagnosticMode.FlowAwareShaping, DiagnosticMode.OutboundFreeze
    });

    private static readonly IReadOnlyDictionary<string, DiagnosticMode> LegacyLabels = new Dictionary<string, DiagnosticMode>
    {
        ["Outbound Freeze"] = DiagnosticMode.OutboundFreeze,
        ["Dual Token Bucket"] = DiagnosticMode.DualTokenBucket,
        ["Bandwidth Shaping"] = DiagnosticMode.BandwidthShaping,
        ["Flow-Aware Shaping"] = DiagnosticMode.FlowAwareShaping,
        ["Inbound Bandwidth Shaping"] = DiagnosticMode.InboundBandwidthShaping,
        ["Correlated Inbound Jitter"] = DiagnosticMode.CorrelatedInboundJitter,
        ["Packet Size-Aware Shaping"] = DiagnosticMode.PacketSizeAwareShaping,
        ["Asymmetric Duplex Shaping"] = DiagnosticMode.AsymmetricDuplexShaping,
        ["Controlled Packet Reordering — EXPERIMENTAL"] = DiagnosticMode.ControlledPacketReordering
    };
    public static IReadOnlyDictionary<string, DiagnosticMode> Choices { get; } =
        PersonalRanking250ms.Concat(LegacyLabels.Values.Except(PersonalRanking250ms))
            .ToDictionary(Label, mode => mode);

    private static string Label(DiagnosticMode mode)
    {
        int rank = PersonalRanking250ms.ToList().IndexOf(mode);
        string name = LegacyLabels.Single(p => p.Value == mode).Key;
        return rank < 0 ? name : $"{rank + 1}º {name}";
    }

    public static bool TryParse(string name, out DiagnosticMode mode) =>
        Choices.TryGetValue(name, out mode) || LegacyLabels.TryGetValue(name, out mode);
    public static DiagnosticMode Parse(string name) => TryParse(name, out var mode) ? mode : DiagnosticMode.OutboundFreeze;
    public static string Name(DiagnosticMode mode) => Label(LegacyLabels.Values.Contains(mode) ? mode : DiagnosticMode.OutboundFreeze);
}
