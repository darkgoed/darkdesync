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
    public async Task FreezeQueue_IsBounded_AndSafetyStopDeliversOnce()
    {
        using var engine = DiagnosticModesTests.Engine(); DiagnosticModesTests.Authorize(engine);
        typeof(PacketEngine).GetField("_isRunning", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(engine, true); // Model a live strict capture epoch; sender is overridden.
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ErrorOccurred += reason => stopped.TrySetResult(reason);
        engine.ActiveDiagnosticMode = DiagnosticMode.OutboundFreeze;
        engine.StateMachine.TryActivate();
        for (int i = 0; i < 520; i++) DiagnosticModesTests.Capture(engine);
        Assert.Contains("limite", await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(512, engine.PacketsThrottled);
        Assert.False(engine.IsRunning);
        engine.FlushThrottleQueue();
        engine.FlushThrottleQueue();
        Assert.Equal(520, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsDropped);
    }
}
