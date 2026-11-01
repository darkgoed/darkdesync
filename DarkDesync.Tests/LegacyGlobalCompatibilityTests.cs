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
    public void SettingsNeverRestoreGlobalSelectionOrConsent_DefaultPulseIs150()
    {
        Assert.Equal(FilterMode.Outbound, new AppSettings { FilterMode = FilterMode.LegacyGlobalOutbound }.Validate().FilterMode);
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
