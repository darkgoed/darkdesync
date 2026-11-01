using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

// Loopback is not a blanket authorization: it can contain unrelated local services.
public sealed class PacketEngineLoopbackTests
{
    [Fact]
    public void UnidentifiedLoopback_DoesNotLoseOrRetainPackets()
    {
        using var engine = DiagnosticModesTests.Engine();
        engine.SendOverride = (_, _, _) => true;
        engine.ActiveDiagnosticMode = DiagnosticMode.BurstLoss;
        engine.BurstLossPercentage = 100;
        engine.StateMachine.TryActivate();
        DiagnosticModesTests.Capture(engine, loopback: true);
        Assert.Equal(1, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsDropped);
    }

    [Fact]
    public void SamePortsDifferentRemoteEndpoint_DoNotAuthorizeLoopback()
    {
        using var engine = DiagnosticModesTests.Engine();
        DiagnosticModesTests.Authorize(engine);
        engine.EndpointsOverride = _ => [DiagnosticModesTests.Endpoint() with { RemoteAddr = 0x0300007f }];
        engine.ActiveDiagnosticMode = DiagnosticMode.OutboundFreeze;
        engine.StateMachine.TryActivate();
        DiagnosticModesTests.Capture(engine);
        Assert.Equal(1, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsThrottled);
    }
}
