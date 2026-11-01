using System.Diagnostics;
using System.Reflection;
using DarkDesync.Core;
using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public sealed class LegacyGlobalCompatibilityTests
{
    private static void Set(PacketEngine engine, string field, object value) =>
        typeof(PacketEngine).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, value);

    // Driver-free model of an already consented, bounded lease. Positive native opening
    // is exercised separately on the isolated Windows runner, never in ordinary unit CI.
    private static void ModelLease(PacketEngine engine, bool expired = false)
    {
        engine.UpdateFilter(FilterMode.LegacyGlobalOutbound);
        Set(engine, "_stopping", false);
        Set(engine, "_isRunning", true); // Model a capture epoch; sender remains the explicit test override.
        Set(engine, "_legacyPulseActive", true);
        Set(engine, "_legacyDeadlineTicks", expired ? long.MinValue : Stopwatch.GetTimestamp() + 800 * Stopwatch.Frequency / 1000);
        engine.StateMachine.TryActivate();
        engine.SendOverride = (_, _, _) => true;
    }

    [Fact]
    public void GlobalWithoutConsentOrWithoutSelectedMode_NeverOpensHandle()
    {
        using var engine = DiagnosticModesTests.Engine();
        engine.StateMachine.TryActivate();
        Assert.False(engine.BeginLegacyGlobalPulse(150, true)); // Strict mode cannot become global via consent alone.
        engine.StateMachine.ForceIdle();
        engine.UpdateFilter(FilterMode.LegacyGlobalOutbound);
        using var controller = new DesyncController(NullLogger<DesyncController>.Instance, engine.StateMachine, engine);
        Assert.False(controller.TriggerCycle(legacyGlobal: true));
        engine.StateMachine.TryActivate();
        Assert.False(engine.BeginLegacyGlobalPulse(150, false));
        Assert.False(engine.BeginLegacyGlobalPulse(0, true));
        Assert.False(engine.Start(FilterMode.LegacyGlobalOutbound)); // No continuous/global Start path.
        Assert.False(engine.IsRunning); Assert.False(engine.IsCaptureRequested);
        Assert.Equal("false", engine.CurrentFilter);
        Assert.Null(engine.WorkerSnapshot.Capture);
    }

    [Fact]
    public void DelayMillisecondsControlPerPacketNotEngineLifetime()
    {
        using var engine = DiagnosticModesTests.Engine();
        DiagnosticModesTests.Authorize(engine);
        engine.ActiveDiagnosticMode = DiagnosticMode.LatencyDelay;
        engine.StateMachine.TryActivate();
        long ticks = 10 * Stopwatch.Frequency;
        typeof(PacketEngine).GetField("_timestamp", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(engine, (Func<long>)(() => ticks));
        var release = typeof(PacketEngine).GetMethod("TryReleaseNext", BindingFlags.NonPublic | BindingFlags.Instance)!;
        bool Released(long at) => (bool)release.Invoke(engine, [at])!;

        engine.GlobalDelayMs = 150;
        Assert.True(DiagnosticModesTests.Capture(engine));
        Assert.Equal(1, engine.PacketsThrottled);
        Assert.False(Released(ticks + (149L * Stopwatch.Frequency / 1000)));
        Assert.True(Released(ticks + (150L * Stopwatch.Frequency / 1000)));
        Assert.Equal(1, engine.PacketsPassedThrough);

        engine.GlobalDelayMs = 250; // Live slider update affects only newly queued packets.
        ticks += Stopwatch.Frequency;
        Assert.True(DiagnosticModesTests.Capture(engine));
        Assert.False(Released(ticks + 249L * Stopwatch.Frequency / 1000));
        Assert.True(Released(ticks + 250L * Stopwatch.Frequency / 1000));
        Assert.Equal(2, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsDropped);
        Assert.Equal(DesyncStateMachine.State.Active, engine.StateMachine.Current);
        engine.GlobalDelayMs = 0;
        Assert.False(DiagnosticModesTests.Capture(engine));
        Assert.Equal(3, engine.PacketsPassedThrough);
    }

    [Fact]
    public void DelaySettingClampsAndPersistsSeparatelyFromPulseDuration()
    {
        Assert.Equal(150, new AppSettings().GlobalDelayMs);
        Assert.Equal(0, new AppSettings { GlobalDelayMs = -50 }.Validate().GlobalDelayMs);
        Assert.Equal(3000, new AppSettings { GlobalDelayMs = 20000 }.Validate().GlobalDelayMs);
        var saved = new AppSettings { GlobalDelayMs = 475, DiagnosticDurationMs = 150,
            DiagnosticMode = DiagnosticMode.LatencyDelay }.Validate();
        Assert.Equal(475, saved.GlobalDelayMs);
        Assert.Equal(150, saved.DiagnosticDurationMs);
        Assert.Equal(DiagnosticMode.LatencyDelay, saved.DiagnosticMode);
    }

    [Fact]
    public void ContinuousGlobalNeedsSelectedModeNotAdditionalConsent()
    {
        using var engine = DiagnosticModesTests.Engine();
        Assert.False(engine.BeginLegacyGlobalContinuous(DiagnosticMode.None));
        Assert.False(engine.IsCaptureRequested);
        Assert.False(engine.IsRunning);
        Assert.Equal(DesyncStateMachine.State.Idle, engine.StateMachine.Current);
    }

    [Fact]
    public void StrictUnknownUdpPasses_ConsentedLeaseDropsWithoutPidOrFlow_ExpiryPasses()
    {
        using var engine = DiagnosticModesTests.Engine();
        engine.SendOverride = (_, _, _) => true;
        engine.ActiveDiagnosticMode = DiagnosticMode.BurstLoss; engine.BurstLossPercentage = 100;
        engine.StateMachine.TryActivate();
        DiagnosticModesTests.Capture(engine, protocol: 17);
        Assert.Equal(0, engine.PacketsDropped); Assert.Equal(1, engine.PacketsPassedThrough);
        engine.StateMachine.ForceIdle();
        ModelLease(engine);
        engine.ActiveDiagnosticMode = DiagnosticMode.BurstLoss;
        DiagnosticModesTests.Capture(engine, protocol: 17);
        Assert.Equal(1, engine.UdpPacketsDropped); Assert.Equal(0, engine.ObservedUdpFlowCount);
        DiagnosticModesTests.Capture(engine, outbound: false, protocol: 17);
        Assert.Equal(1, engine.PacketsDropped); // Global outbound still never affects inbound.
        Set(engine, "_legacyDeadlineTicks", long.MinValue);
        DiagnosticModesTests.Capture(engine, protocol: 17);
        Assert.Equal(1, engine.PacketsDropped); // Stalled watchdog/controller cannot extend effects.
        engine.EndLegacyGlobalPulse();
        Assert.False(engine.IsLegacyGlobalPulse); Assert.Equal("false", engine.CurrentFilter);
    }

    [Fact]
    public void GlobalFreezeRetainsNotDrops_ExpiredLeaseAndCancelFlushOnce()
    {
        using var engine = DiagnosticModesTests.Engine(); ModelLease(engine);
        engine.ActiveDiagnosticMode = DiagnosticMode.OutboundFreeze;
        DiagnosticModesTests.Capture(engine, protocol: 17);
        Assert.Equal(1, engine.UdpPacketsHeld); Assert.Equal(0, engine.PacketsDropped);
        Set(engine, "_legacyDeadlineTicks", long.MinValue);
        DiagnosticModesTests.Capture(engine, protocol: 17); // Expiry immediately flushes prior held packet.
        Assert.Equal(2, engine.PacketsPassedThrough);
        engine.EndLegacyGlobalPulse(); engine.EndLegacyGlobalPulse();
        Assert.Equal(2, engine.PacketsPassedThrough);
    }

    [Fact]
    public void SwitchingBackToStrictRevokesLeaseBeforeAnyOtherPacket()
    {
        using var engine = DiagnosticModesTests.Engine(); ModelLease(engine);
        engine.ActiveDiagnosticMode = DiagnosticMode.BurstLoss; engine.BurstLossPercentage = 100;
        engine.UpdateFilter(FilterMode.Outbound);
        Assert.False(engine.IsLegacyGlobalPulse); Assert.False(engine.IsRunning);
        DiagnosticModesTests.Capture(engine, protocol: 17);
        Assert.Equal(0, engine.PacketsDropped);
    }

    [Fact]
    public async Task FailedGlobalReinjectionClosesLease_NotMerelyCountedAsWorking()
    {
        using var engine = DiagnosticModesTests.Engine(); ModelLease(engine);
        engine.ActiveDiagnosticMode = DiagnosticMode.BurstLoss; engine.BurstLossPercentage = 0;
        engine.SendOverride = (_, _, _) => false;
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ErrorOccurred += e => stopped.TrySetResult(e);
        DiagnosticModesTests.Capture(engine, protocol: 17);
        Assert.Contains("Reinjeção", await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(engine.IsLegacyGlobalPulse); Assert.Equal("false", engine.CurrentFilter);
        Assert.Equal(1, engine.PacketsDropped); Assert.Equal(0, engine.PacketsPassedThrough);
    }

    [Fact]
    public void GlobalModeCanBeRememberedButNeverAutomaticallyStarts()
    {
        Assert.Equal(FilterMode.LegacyGlobalOutbound, new AppSettings { FilterMode = FilterMode.LegacyGlobalOutbound }.Validate().FilterMode);
        Assert.Equal(FilterMode.Outbound, new AppSettings().FilterMode);
        Assert.Equal(150, new AppSettings().DiagnosticDurationMs);
    }

    [Fact]
    public void GlobalUsesExistingSpaceEdge_OnePressUntilRelease()
    {
        using var hotkey = new HotkeyService(NullLogger<HotkeyService>.Instance);
        Assert.True(hotkey.AcceptKeyTransition(0x20, true));
        for (int i = 0; i < 200; i++) Assert.False(hotkey.AcceptKeyTransition(0x20, true));
        Assert.False(hotkey.AcceptKeyTransition(0x20, false));
        Assert.True(hotkey.AcceptKeyTransition(0x20, true));
    }
}
