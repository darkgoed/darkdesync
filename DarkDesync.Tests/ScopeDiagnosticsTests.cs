using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public sealed class ScopeDiagnosticsTests
{
    [Fact]
    public void DiagnosticsSeparateTcpAndUdpCountersFromAuthorizedEffects()
    {
        using var engine = DiagnosticModesTests.Engine(); DiagnosticModesTests.Authorize(engine);
        engine.UdpFlowsOverride = () => [DiagnosticModesTests.Endpoint() with { Protocol = 17 }];
        engine.UdpBindingOverride = _ => true;
        engine.StateMachine.TryActivate();
        engine.ActiveDiagnosticMode = DiagnosticMode.BurstLoss; engine.BurstLossPercentage = 100;
        DiagnosticModesTests.Capture(engine);
        DiagnosticModesTests.Capture(engine, protocol: 17);
        Assert.Equal(1, engine.UdpPacketsDropped);
        engine.ActiveDiagnosticMode = DiagnosticMode.OutboundFreeze;
        DiagnosticModesTests.Capture(engine, protocol: 17);
        Assert.Equal(1, engine.UdpPacketsHeld);
        Assert.Contains("TCP: capturados 1, descartados 1, retidos 0", engine.ProtocolCounters);
        Assert.Contains("UDP: capturados 2, descartados 1, retidos 1", engine.ProtocolCounters);
        engine.StateMachine.TryBeginRestore(); engine.FlushThrottleQueue();
        Assert.Equal(1, engine.PacketsPassedThrough);
    }

    [Fact]
    public void EmptyScopeExplicitlyGuidesObservationBeforeExperience_NotOldUdpReconstruction()
    {
        using var engine = DiagnosticModesTests.Engine();
        engine.ObserveTarget(0);
        Assert.Equal(0u, engine.ScopeStatus.ProcessId);
        Assert.False(engine.ScopeStatus.Running); Assert.False(engine.ScopeStatus.Observing);
        Assert.Equal("false", engine.ScopeStatus.Filter);
        Assert.Contains("antes de entrar na experiência", engine.ScopeStatus.Reason);
        Assert.Contains("não é reconstruído", engine.ScopeStatus.Reason);
        Assert.Equal(0, engine.ScopeStatus.UdpScoped);
    }

    [Fact]
    public void TupleDetailsShowProtocolsBothEndpointsAndNoPortOnlyAuthorization()
    {
        string text = CaptureScopeStatus.Describe([DiagnosticModesTests.Endpoint(), DiagnosticModesTests.Endpoint(444) with { Protocol = 17 }]);
        Assert.Contains("TCP 127.0.0.1:40000 ↔ 127.0.0.2:443", text);
        Assert.Contains("UDP 127.0.0.1:40000 ↔ 127.0.0.2:444", text);
    }
}
