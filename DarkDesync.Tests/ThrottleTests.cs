using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public class ThrottleTests
{
    [Fact]
    public void ThrottleProperties_DefaultAndClampedCorrectly()
    {
        using var engine = DiagnosticModesTests.Engine();
        Assert.False(engine.ThrottleEnabled);
        Assert.Equal(1000, engine.ThrottleTimeframeMs);
        Assert.Equal(100, engine.ThrottleChance);
        Assert.Equal(0, engine.ThrottleReleaseDelayMs);
        engine.ThrottleTimeframeMs = 500; engine.ThrottleChance = 75; engine.ThrottleReleaseDelayMs = 15;
        Assert.Equal(500, engine.ThrottleTimeframeMs);
        Assert.Equal(75, engine.ThrottleChance);
        Assert.Equal(15, engine.ThrottleReleaseDelayMs);
    }

    [Fact]
    public void FreezeQueue_IsBounded_AndRestoreDeliversOnce()
    {
        using var engine = DiagnosticModesTests.Engine(); DiagnosticModesTests.Authorize(engine);
        engine.ActiveDiagnosticMode = DiagnosticMode.OutboundFreeze;
        engine.StateMachine.TryActivate();
        for (int i = 0; i < 520; i++) DiagnosticModesTests.Capture(engine);
        Assert.Equal(512, engine.PacketsThrottled);
        Assert.Equal(8, engine.PacketsPassedThrough);
        engine.StateMachine.TryBeginRestore();
        engine.FlushThrottleQueue();
        engine.FlushThrottleQueue();
        Assert.Equal(520, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsDropped);
    }
}
