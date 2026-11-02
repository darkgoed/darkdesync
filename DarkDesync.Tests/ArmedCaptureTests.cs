using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DarkDesync.Core;
using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public sealed class ArmedCaptureTests
{
    internal static void Set(PacketEngine engine, string field, object value) =>
        typeof(PacketEngine).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, value);

    // Model only native handle creation; use the production arm/pulse/packet paths.
    internal static void ModelArm(PacketEngine engine, bool global = true)
    {
        engine.UpdateFilter(global ? FilterMode.LegacyGlobalOutbound : FilterMode.Outbound);
        typeof(PacketEngine).GetMethod("PrepareArmedCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, null);
        Set(engine, "_captureRequested", true);
        Set(engine, "_isRunning", true);
        Set(engine, "_stopping", false);
        if (global)
        {
            Set(engine, "_legacyPulseActive", true);
            Set(engine, "_legacyDeadlineTicks", long.MaxValue);
        }
        engine.SendOverride = (_, _, _) => true;
    }

    [Theory]
    [InlineData(DiagnosticMode.None)]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    [InlineData(DiagnosticMode.AsymmetricLoss)]
    [InlineData(DiagnosticMode.BurstLoss)]
    [InlineData(DiagnosticMode.LatencyDelay)]
    [InlineData(DiagnosticMode.PacketDrop)]
    public void SelectedEffect_StartPasses_TriggerOnlyWhileArmed_ExpiryKeepsCapture(DiagnosticMode selected)
    {
        using var engine = DiagnosticModesTests.Engine();
        Assert.False(engine.TriggerArmedEffect(selected, 150));
        Assert.False(engine.IsCaptureRequested);
        engine.ActiveDiagnosticMode = selected; // Even an old loaded selection is cleared at arm.
        engine.SetDropPercentage(100);
        engine.BurstLossPercentage = engine.AsymmetricOutboundLossPercentage = 100;
        engine.CycleDropEnabled = engine.IsDropActive = engine.IsThrottleActive = true;
        engine.ThrottleEnabled = engine.ThrottleContinuous = true;
        engine.GlobalDelayMs = 3000;
        ModelArm(engine);
        Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
        Assert.Equal(DesyncStateMachine.State.Armed, engine.StateMachine.Current);
        Assert.False(DiagnosticModesTests.Capture(engine, protocol: 17));
        Assert.Equal(1, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsDropped); Assert.Equal(0, engine.PacketsThrottled);
        if (selected == DiagnosticMode.None)
        {
            Assert.False(engine.TriggerArmedEffect(selected, 150));
            Assert.False(DiagnosticModesTests.Capture(engine));
            return;
        }
        long now = Stopwatch.GetTimestamp();
        Set(engine, "_timestamp", (Func<long>)(() => now));
        Assert.True(engine.TriggerArmedEffect(selected, 150));
        Assert.False(engine.TriggerArmedEffect(selected, 150));
        Assert.True(engine.IsEffectActive);
        Assert.Equal(selected, engine.ActiveDiagnosticMode);
        Assert.True(DiagnosticModesTests.Capture(engine, protocol: 17));
        long dropped = engine.PacketsDropped;
        now += Stopwatch.Frequency; // Packet deadline enforces restoration even if timer is late.
        Assert.False(DiagnosticModesTests.Capture(engine, protocol: 17));
        Assert.Equal(dropped, engine.PacketsDropped);
        Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
        Assert.Equal(DesyncStateMachine.State.Armed, engine.StateMachine.Current);
        Assert.True(engine.IsCaptureRequested); Assert.True(engine.IsRunning);
        Assert.True(engine.TriggerArmedEffect(selected, 150));
        engine.Stop();
        Assert.False(engine.IsEffectActive); Assert.False(engine.IsRunning);
        Assert.Equal(DesyncStateMachine.State.Idle, engine.StateMachine.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictArmed_IgnoresContinuousAndCycleDropUntilPulse(bool throttle)
    {
        using var engine = DiagnosticModesTests.Engine();
        ModelArm(engine, global: false); DiagnosticModesTests.Authorize(engine);
        engine.CycleDropEnabled = true;
        engine.SetDropPercentage(100);
        engine.ThrottleEnabled = engine.ThrottleContinuous = throttle;
        engine.ThrottleChance = 100;
        Assert.False(DiagnosticModesTests.Capture(engine));
        Assert.Equal(1, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsDropped); Assert.Equal(0, engine.PacketsThrottled);
        Assert.True(engine.TriggerArmedEffect(DiagnosticMode.None, 800));
        Assert.True(DiagnosticModesTests.Capture(engine));
        Assert.Equal(throttle ? 1 : 0, engine.PacketsThrottled);
        Assert.Equal(throttle ? 0 : 1, engine.PacketsDropped);
        engine.CancelArmedEffect();
        Assert.False(DiagnosticModesTests.Capture(engine));
        Assert.True(engine.IsRunning);
        Assert.Equal(DesyncStateMachine.State.Armed, engine.StateMachine.Current);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public void FreezeRelease_FifoExactlyOnce(string action)
    {
        using var engine = DiagnosticModesTests.Engine(); ModelArm(engine);
        var sent = new List<byte>();
        engine.SendOverride = (buffer, _, _) => { sent.Add(Marshal.ReadByte(buffer, 23)); return true; };
        Assert.True(engine.TriggerArmedEffect(DiagnosticMode.OutboundFreeze, 800));
        for (ushort id = 1; id <= 8; id++) Assert.True(DiagnosticModesTests.Capture(engine, remotePort: id));
        Assert.Empty(sent);
        if (action == "cancel") engine.CancelArmedEffect();
        else if (action == "stop") engine.Stop();
        else engine.Dispose();
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (byte)i), sent);
        engine.CancelArmedEffect(); engine.FlushThrottleQueue();
        Assert.Equal(8, sent.Count);
        Assert.Equal(0, engine.PacketsDropped);
    }

    [Fact]
    public async Task OldPulseTimer_CannotStopOrRestoreNewStart()
    {
        using var engine = DiagnosticModesTests.Engine(); ModelArm(engine);
        Assert.True(engine.TriggerArmedEffect(DiagnosticMode.OutboundFreeze, 80));
        engine.Stop(); ModelArm(engine);
        long generation = engine.ArmedGeneration;
        Assert.True(engine.TriggerArmedEffect(DiagnosticMode.AsymmetricLoss, 800));
        await Task.Delay(180);
        Assert.Equal(generation, engine.ArmedGeneration);
        Assert.True(engine.IsRunning); Assert.True(engine.IsEffectActive);
        Assert.Equal(DiagnosticMode.AsymmetricLoss, engine.ActiveDiagnosticMode);
    }

    [Fact]
    public async Task Watchdog_RestoresWithoutAnyNewPacket_AndDelayIndependentOfPulse()
    {
        using var engine = DiagnosticModesTests.Engine(); ModelArm(engine);
        engine.GlobalDelayMs = 3000;
        Assert.False(engine.TriggerArmedEffect(DiagnosticMode.LatencyDelay, 0));
        Assert.True(engine.TriggerArmedEffect(DiagnosticMode.LatencyDelay, 50));
        Assert.True(DiagnosticModesTests.Capture(engine));
        var watch = Stopwatch.StartNew();
        while (engine.StateMachine.Current != DesyncStateMachine.State.Armed && watch.ElapsedMilliseconds < 2000)
            await Task.Delay(5);
        Assert.Equal(DesyncStateMachine.State.Armed, engine.StateMachine.Current);
        Assert.Equal(1, engine.PacketsPassedThrough);
        Assert.Equal(3000, engine.GlobalDelayMs);
        Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
        Assert.True(engine.IsRunning);
    }

    [Fact]
    public void Hotkey_EdgeDispatchOnePerPress_AndCallbackAlwaysPassesKeyOn()
    {
        using var hotkey = new HotkeyService(NullLogger<HotkeyService>.Instance);
        int calls = 0;
        hotkey.HotkeyPressed += () => calls++;
        hotkey.DispatchKeyTransition(0x20, true);
        for (int i = 0; i < 200; i++) hotkey.DispatchKeyTransition(0x20, true);
        Assert.Equal(1, calls);
        hotkey.DispatchKeyTransition(0x20, false);
        hotkey.DispatchKeyTransition(0x20, true);
        Assert.Equal(2, calls);
        // nCode < 0 must delegate immediately to the next hook; it cannot consume Space.
        var callback = typeof(HotkeyService).GetMethod("HookCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(IntPtr.Zero, (IntPtr)callback.Invoke(hotkey, [-1, (IntPtr)0x100, IntPtr.Zero])!);
    }
}
