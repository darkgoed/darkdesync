namespace DarkDesync.Core;

/// <summary>
/// Diagnostic network modes for authorized application and endpoint testing.
/// </summary>
public enum DiagnosticMode
{
    /// <summary>No diagnostic mode active (standard cycle or pass-through).</summary>
    None = 0,

    /// <summary>1. Temporarily interrupts outbound test packet transmission.</summary>
    OutboundFreeze = 1,

    /// <summary>2. Simulates intermittent packet loss with configurable duration and percentage.</summary>
    BurstLoss = 2,

    /// <summary>3. Applies independent configurable loss rates for inbound and outbound traffic.</summary>
    AsymmetricLoss = 3,

    /// <summary>4. Holds outgoing packets for a configurable time per packet, while START is active.</summary>
    LatencyDelay = 4
}
