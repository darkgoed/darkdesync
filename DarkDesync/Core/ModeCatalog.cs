namespace DarkDesync.Core;

public static class ModeCatalog
{
    public static IReadOnlyDictionary<string, DiagnosticMode> Choices { get; } = new Dictionary<string, DiagnosticMode>
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
    public static DiagnosticMode Parse(string name) => Choices.TryGetValue(name, out var mode) ? mode : DiagnosticMode.OutboundFreeze;
    public static string Name(DiagnosticMode mode) => Choices.FirstOrDefault(p => p.Value == mode).Key ?? "Outbound Freeze";
}
