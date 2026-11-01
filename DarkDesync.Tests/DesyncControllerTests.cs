using DarkDesync.Core;
using DarkDesync.Models;
using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public class DesyncControllerTests
{
    private readonly DesyncStateMachine _stateMachine;
    private readonly DesyncController _controller;

    public DesyncControllerTests()
    {
        _stateMachine = new DesyncStateMachine();
        _controller = new DesyncController(
            NullLogger<DesyncController>.Instance,
            _stateMachine,
            null // Headless, no raw driver needed for controller logic tests
        );
    }

    [Fact]
    public async Task TriggerCycle_RunsLifecycleAndRestoresIdle()
    {
        var profile = new IntensityProfile
        {
            Name = "TestShort",
            DurationMs = 100, // Short duration for fast test
            DropPercentage = 100
        };
        _controller.ActiveProfile = profile;

        CycleRecord? recordedCycle = null;
        _controller.CycleFinished += rec => recordedCycle = rec;

        bool started = _controller.TriggerCycle();
        Assert.True(started);
        Assert.Equal(DesyncStateMachine.State.Active, _controller.CurrentState);

        // Wait for cycle to complete + restoration settling (100ms + 40ms settling + padding)
        await Task.Delay(250);

        Assert.Equal(DesyncStateMachine.State.Idle, _controller.CurrentState);
        Assert.NotNull(recordedCycle);
        Assert.Equal("TestShort", recordedCycle.ProfileName);
        Assert.True(recordedCycle.CompletedNormally);
    }

    [Fact]
    public async Task TriggerCycle_RapidRepetitionIgnored()
    {
        var profile = new IntensityProfile
        {
            Name = "TestRepeat",
            DurationMs = 300,
            DropPercentage = 100
        };
        _controller.ActiveProfile = profile;

        bool first = _controller.TriggerCycle();
        Assert.True(first);

        // Simulate rapid key presses during active cycle
        bool second = _controller.TriggerCycle();
        bool third = _controller.TriggerCycle();
        bool fourth = _controller.TriggerCycle();

        Assert.False(second);
        Assert.False(third);
        Assert.False(fourth);

        await Task.Delay(400);
        Assert.Equal(DesyncStateMachine.State.Idle, _controller.CurrentState);
    }

    [Fact]
    public async Task CancelCycle_ImmediatelyRestoresIdle()
    {
        var profile = new IntensityProfile
        {
            Name = "TestCancel",
            DurationMs = 2000,
            DropPercentage = 100
        };
        _controller.ActiveProfile = profile;

        bool started = _controller.TriggerCycle();
        Assert.True(started);
        Assert.Equal(DesyncStateMachine.State.Active, _controller.CurrentState);

        // Cancel while running
        await Task.Delay(50);
        _controller.CancelCycle();

        Assert.Equal(DesyncStateMachine.State.Idle, _controller.CurrentState);

        // Confirm we can immediately trigger a new cycle after cancel
        bool newCycle = _controller.TriggerCycle();
        Assert.True(newCycle);

        _controller.CancelCycle();
    }
}
