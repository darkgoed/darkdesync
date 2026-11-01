using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DarkDesync.Core;
using DarkDesync.Services;
using Xunit;

namespace DarkDesync.Tests;

public sealed class AuditRegressionTests
{
    private sealed class ZeroRandom : Random { public override int Next(int maxValue) => 0; }
    private static long Ticks(long ms) => ms * Stopwatch.Frequency / 1000;
    private static void Set(PacketEngine engine, string field, object value) =>
        typeof(PacketEngine).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, value);
    private static ConcurrentQueue<ThrottledPacket> Queue(PacketEngine engine) =>
        (ConcurrentQueue<ThrottledPacket>)typeof(PacketEngine).GetField("_throttleQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
    private static bool Release(PacketEngine engine, long at) =>
        (bool)typeof(PacketEngine).GetMethod("TryReleaseNext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [at])!;
    private static bool Capture(PacketEngine engine, int id, bool outbound = true)
    {
        byte[] bytes = DiagnosticModesTests.Packet(outbound, protocol: 17);
        BitConverter.GetBytes(id).CopyTo(bytes, 36);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            return engine.ProcessCapturedPacket(buffer, (uint)bytes.Length,
                new WinDivertNative.Address { Flags = outbound ? 1u << 17 : 0 }, new ZeroRandom());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static List<int> ModelGlobal(PacketEngine engine, DiagnosticMode mode, Func<long> clock)
    {
        engine.UpdateFilter(FilterMode.LegacyGlobalOutbound);
        Set(engine, "_timestamp", clock);
        Set(engine, "_stopping", false);
        Set(engine, "_legacyPulseActive", true);
        Set(engine, "_legacyDeadlineTicks", long.MaxValue);
        engine.ActiveDiagnosticMode = mode;
        engine.StateMachine.TryActivate();
        var sent = new List<int>();
        engine.SendOverride = (p, _, _) => { sent.Add(Marshal.ReadInt32(p, 36)); return true; };
        return sent;
    }

    [Theory]
    [InlineData(0)] [InlineData(50)] [InlineData(150)] [InlineData(3000)]
    public void LagDeadline_IsPerPacket_NotStartLifetime(int delay)
    {
        using var engine = DiagnosticModesTests.Engine();
        long now = Ticks(10000);
        var sent = ModelGlobal(engine, DiagnosticMode.LatencyDelay, () => now);
        engine.GlobalDelayMs = delay;
        Assert.Equal(delay > 0, Capture(engine, 1));
        if (delay > 0)
        {
            Assert.False(Release(engine, now + Ticks(delay) - 1));
            Assert.True(Release(engine, now + Ticks(delay)));
        }
        Assert.Equal(new[] { 1 }, sent);
        Assert.Equal(DesyncStateMachine.State.Active, engine.StateMachine.Current);
        Assert.Equal(0, engine.PacketsDropped);
    }

    [Fact]
    public void SliderReduction_RebasesPendingFromArrival_WithoutHeadOfLineOrSpacing()
    {
        using var engine = DiagnosticModesTests.Engine();
        long now = 0;
        var sent = ModelGlobal(engine, DiagnosticMode.LatencyDelay, () => now);
        engine.ThrottleReleaseDelayMs = 200;
        engine.GlobalDelayMs = 3000;
        Capture(engine, 1);
        now = Ticks(10); Capture(engine, 2);
        now = Ticks(100);
        engine.GlobalDelayMs = 50;
        Assert.Equal(new[] { 1, 2 }, sent);
        Capture(engine, 3);
        Assert.False(Release(engine, Ticks(149)));
        Assert.True(Release(engine, Ticks(150)));
        Assert.Equal(new[] { 1, 2, 3 }, sent);
        Assert.Empty(Queue(engine));
    }

    [Fact]
    public void SliderIncrease_RebasesPending_ZeroFlushesInOrder()
    {
        using var engine = DiagnosticModesTests.Engine();
        long now = 0;
        var sent = ModelGlobal(engine, DiagnosticMode.LatencyDelay, () => now);
        engine.GlobalDelayMs = 50; Capture(engine, 1);
        now = Ticks(20); engine.GlobalDelayMs = 3000; Capture(engine, 2);
        Assert.False(Release(engine, Ticks(50)));
        Assert.False(Release(engine, Ticks(2999)));
        Assert.True(Release(engine, Ticks(3000)));
        engine.GlobalDelayMs = 0;
        Capture(engine, 3);
        Assert.Equal(new[] { 1, 2, 3 }, sent);
        Assert.Empty(Queue(engine));
    }

    [Fact]
    public void Lag_DoesNotApplyThrottleSpacingBetweenDuePackets()
    {
        using var engine = DiagnosticModesTests.Engine();
        var sent = ModelGlobal(engine, DiagnosticMode.LatencyDelay, () => 0);
        engine.ThrottleReleaseDelayMs = 200;
        Capture(engine, 1); Capture(engine, 2);
        Assert.True(Release(engine, Ticks(150)));
        Assert.True(Release(engine, Ticks(150)));
        Assert.Equal(new[] { 1, 2 }, sent);
    }

    [Theory]
    [InlineData(DiagnosticMode.LatencyDelay)]
    [InlineData(DiagnosticMode.None)] // Legacy throttle scheduling is tested without a global lease below.
    public void QueueSaturation_IsBoundedAndNeverOvertakesOldest(DiagnosticMode mode)
    {
        using var engine = DiagnosticModesTests.Engine();
        var sent = ModelGlobal(engine, mode, () => 0);
        if (mode == DiagnosticMode.None)
        {
            Set(engine, "_filterMode", FilterMode.Outbound);
            Set(engine, "_legacyPulseActive", false);
            engine.CycleDropEnabled = false;
            engine.IsThrottleActive = true;
        }
        for (int id = 0; id < 513; id++) Capture(engine, id);
        Assert.Equal(512, Queue(engine).Count);
        Assert.Equal(new[] { 0 }, sent);
        engine.Stop(); engine.Stop();
        Assert.Equal(Enumerable.Range(0, 513), sent);
        Assert.Equal(513, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsDropped);
    }

    [Theory]
    [InlineData(DiagnosticMode.LatencyDelay)]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    public void UnexpectedInbound_NeverFlushesOrChangesOutboundQueue(DiagnosticMode mode)
    {
        using var engine = DiagnosticModesTests.Engine();
        var sent = ModelGlobal(engine, mode, () => 0);
        Capture(engine, 1);
        Capture(engine, 2, outbound: false);
        Assert.Equal(new[] { 2 }, sent);
        Assert.Single(Queue(engine));
        engine.Stop();
        Assert.Equal(new[] { 2, 1 }, sent); // Directions are independent; outbound order remains unchanged.
    }

    [Theory]
    [InlineData(0)] [InlineData(50)] [InlineData(100)]
    public void GlobalDisabled_IgnoresAllLegacyDropAndThrottleFlags(int percent)
    {
        using var engine = DiagnosticModesTests.Engine();
        var sent = ModelGlobal(engine, DiagnosticMode.None, () => 0);
        engine.SetDropPercentage(percent);
        engine.CycleDropEnabled = true;
        engine.ThrottleEnabled = engine.ThrottleContinuous = engine.IsDropActive = engine.IsThrottleActive = true;
        Capture(engine, 1);
        Assert.Equal(new[] { 1 }, sent);
        Assert.Equal(0, engine.PacketsDropped);
        Assert.Empty(Queue(engine));
        engine.Stop();
        Assert.False(engine.IsDropActive); Assert.False(engine.IsThrottleActive);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(50, 1)] [InlineData(100, 1)]
    public void ExplicitDrop_IsSeparateFromDisabledAndOverridesThrottle(int percent, int dropped)
    {
        using var engine = DiagnosticModesTests.Engine();
        ModelGlobal(engine, DiagnosticMode.PacketDrop, () => 0);
        engine.SetDropPercentage(percent);
        engine.ThrottleEnabled = engine.ThrottleContinuous = true;
        Capture(engine, 1);
        Assert.Equal(dropped, engine.PacketsDropped);
        Assert.Equal(1 - dropped, engine.PacketsPassedThrough);
        Assert.Empty(Queue(engine));
        Assert.Equal(DiagnosticMode.PacketDrop, new AppSettings { DiagnosticMode = DiagnosticMode.PacketDrop }.Validate().DiagnosticMode);
    }

    [Fact]
    public async Task FreezeOverflow_FailsOpenByStopping_FlushesExactlyOnce()
    {
        using var engine = DiagnosticModesTests.Engine();
        var sent = ModelGlobal(engine, DiagnosticMode.OutboundFreeze, () => 0);
        Set(engine, "_isRunning", true);
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ErrorOccurred += reason => stopped.TrySetResult(reason);
        for (int id = 0; id < 513; id++) Capture(engine, id);
        Assert.Contains("limite", await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(engine.IsRunning);
        Assert.Contains("limite", engine.ScopeStatus.Reason);
        Assert.Empty(Queue(engine));
        Assert.Equal(Enumerable.Range(0, 513), sent);
        Assert.Equal(0, engine.PacketsDropped);
    }

    [Theory]
    [InlineData(DiagnosticMode.LatencyDelay)]
    [InlineData(DiagnosticMode.PacketDrop)]
    public void NewEffects_StillRejectUnauthorizedStrictTraffic(DiagnosticMode mode)
    {
        using var engine = DiagnosticModesTests.Engine();
        DiagnosticModesTests.Authorize(engine);
        engine.ActiveDiagnosticMode = mode;
        engine.StateMachine.TryActivate();
        DiagnosticModesTests.Capture(engine, remotePort: 444);
        DiagnosticModesTests.Capture(engine, protocol: 17);
        DiagnosticModesTests.Capture(engine, fragment: true);
        Assert.Equal(3, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsDropped);
        Assert.Empty(Queue(engine));
    }

    [Fact]
    public async Task StrictReinjectionFailure_StopsInsteadOfReportingWorkingCapture()
    {
        using var engine = DiagnosticModesTests.Engine();
        DiagnosticModesTests.Authorize(engine);
        Set(engine, "_isRunning", true);
        engine.ActiveDiagnosticMode = DiagnosticMode.PacketDrop;
        engine.SetDropPercentage(0);
        engine.StateMachine.TryActivate();
        engine.SendOverride = (_, _, _) => false;
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ErrorOccurred += reason => stopped.TrySetResult(reason);
        DiagnosticModesTests.Capture(engine);
        Assert.Contains("Reinjeção", await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(engine.IsRunning);
        Assert.Equal(1, engine.PacketsDropped);
        Assert.Equal(0, engine.PacketsPassedThrough);
    }
}
