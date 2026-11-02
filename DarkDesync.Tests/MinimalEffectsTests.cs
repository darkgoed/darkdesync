using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public sealed class MinimalEffectsTests
{
    private static long Ticks(int ms) => (long)ms * Stopwatch.Frequency / 1000;
    private static bool Release(PacketEngine engine, long now) => (bool)typeof(PacketEngine)
        .GetMethod("TryReleaseNext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [now])!;

    [Theory]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    [InlineData(DiagnosticMode.MicroFreeze)]
    [InlineData(DiagnosticMode.SmoothDelay)]
    [InlineData(DiagnosticMode.SoftJitter)]
    public void AllEffects_200msPulse_Fifo_ExpiryAndStop_NoIntentionalLoss(DiagnosticMode mode)
    {
        using var engine = DiagnosticModesTests.Engine();
        Assert.False(engine.TriggerArmedEffect(mode, 200));
        ArmedCaptureTests.ModelArm(engine);
        long now = Ticks(1000);
        ArmedCaptureTests.Set(engine, "_timestamp", (Func<long>)(() => now));
        var sent = new List<int>();
        engine.SendOverride = (p, _, _) => { sent.Add((Marshal.ReadByte(p, 22) << 8) | Marshal.ReadByte(p, 23)); return true; };
        Assert.False(DiagnosticModesTests.Capture(engine, remotePort: 1));
        Assert.Equal(new[] { 1 }, sent);
        Assert.True(engine.TriggerArmedEffect(mode, 200));
        Assert.False(engine.TriggerArmedEffect(mode, 200));
        engine.ThrottleReleaseDelayMs = 200; // Hidden old spacing must not affect timed modes.
        for (ushort id = 2; id <= 20; id++)
        {
            Assert.True(DiagnosticModesTests.Capture(engine, remotePort: id));
            now += Ticks(1);
        }
        Assert.Equal(new[] { 1 }, sent);
        now = Ticks(1199);
        while (Release(engine, now)) { }
        if (mode == DiagnosticMode.OutboundFreeze) Assert.Single(sent);
        else Assert.Equal(20, sent.Count);
        Assert.True(engine.IsEffectActive);
        Assert.Equal(DesyncStateMachine.State.Active, engine.StateMachine.Current);
        now = Ticks(1200);
        Release(engine, now);
        Assert.Equal(Enumerable.Range(1, 20), sent);
        Assert.Equal(DesyncStateMachine.State.Armed, engine.StateMachine.Current);
        Assert.True(engine.IsRunning);
        Assert.True(engine.TriggerArmedEffect(mode, 200));
        Assert.True(DiagnosticModesTests.Capture(engine, remotePort: 21));
        engine.Stop(); engine.FlushThrottleQueue();
        Assert.Equal(Enumerable.Range(1, 21), sent);
        Assert.Equal(0, engine.PacketsDropped);
        Assert.False(engine.IsRunning); Assert.False(engine.IsCaptureRequested);
    }

    [Theory]
    [InlineData(DiagnosticMode.MicroFreeze, 20)]
    [InlineData(DiagnosticMode.SmoothDelay, 30)]
    public void FixedTiming_IsMeasuredFromPulseBoundaryOrPacket(DiagnosticMode mode, int releaseMs)
    {
        using var engine = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(engine);
        long now = Ticks(1000);
        ArmedCaptureTests.Set(engine, "_timestamp", (Func<long>)(() => now));
        Assert.True(engine.TriggerArmedEffect(mode, 200));
        now += Ticks(5);
        Assert.True(DiagnosticModesTests.Capture(engine));
        int deadline = mode == DiagnosticMode.MicroFreeze ? releaseMs : 5 + releaseMs;
        now = Ticks(1000 + deadline - 1); Assert.False(Release(engine, now));
        now = Ticks(1000 + deadline); Assert.True(Release(engine, now));
        Assert.Equal(1, engine.PacketsPassedThrough); Assert.Equal(0, engine.PacketsDropped);
    }

    [Theory]
    [InlineData(DiagnosticMode.MicroFreeze)]
    [InlineData(DiagnosticMode.SmoothDelay)]
    [InlineData(DiagnosticMode.SoftJitter)]
    public void DeadlineFlushesEvenLastPacket_AndSaturationReleasesOldestFifo(DiagnosticMode mode)
    {
        using var engine = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(engine);
        long now = Ticks(1000);
        ArmedCaptureTests.Set(engine, "_timestamp", (Func<long>)(() => now));
        var sent = new List<int>();
        engine.SendOverride = (p, _, _) => { sent.Add((Marshal.ReadByte(p, 22) << 8) | Marshal.ReadByte(p, 23)); return true; };
        Assert.True(engine.TriggerArmedEffect(mode, 200));
        now = Ticks(1199);
        for (ushort id = 1; id <= 600; id++) Assert.True(DiagnosticModesTests.Capture(engine, remotePort: id));
        Assert.Equal(Enumerable.Range(1, 88), sent);
        now = Ticks(1200); Release(engine, now);
        Assert.Equal(Enumerable.Range(1, 600), sent);
        Assert.Equal(0, engine.PacketsDropped); Assert.Equal(600, engine.PacketsPassedThrough);
        Assert.True(engine.IsRunning);
    }

    [Fact]
    public void SoftJitter_VariesWithinBounds_AndCannotOvertakeEarlierPacket()
    {
        using var engine = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(engine);
        long now = Ticks(1000);
        ArmedCaptureTests.Set(engine, "_timestamp", (Func<long>)(() => now));
        Assert.True(engine.TriggerArmedEffect(DiagnosticMode.SoftJitter, 200));
        var process = typeof(PacketEngine).GetMethod("ProcessCapturedPacket", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pointer = Marshal.AllocHGlobal(40);
        var random = new Random(71);
        try
        {
            for (ushort id = 1; id <= 100; id++)
            {
                Marshal.Copy(DiagnosticModesTests.Packet(true, id), 0, pointer, 40);
                process.Invoke(engine, [pointer, 40u, new WinDivertNative.Address { Flags = 1u << 17 }, random]);
            }
        }
        finally { Marshal.FreeHGlobal(pointer); }
        var queue = (System.Collections.IEnumerable)typeof(PacketEngine).GetField("_throttleQueue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        var deadlines = queue.Cast<object>().Select(p => (long)p.GetType().GetProperty("ReleaseAtTicks")!.GetValue(p)!).ToArray();
        Assert.True(deadlines.Distinct().Count() > 1);
        Assert.All(deadlines, d => Assert.InRange(d - now, Ticks(8), Ticks(24)));
        Assert.True(deadlines.Zip(deadlines.Skip(1)).Any(pair => pair.First > pair.Second));
        var sent = new List<byte>();
        engine.SendOverride = (p, _, _) => { sent.Add(Marshal.ReadByte(p, 23)); return true; };
        now += Ticks(7); Assert.False(Release(engine, now));
        for (int ms = 8; ms <= 24; ms++) { now = Ticks(1000 + ms); while (Release(engine, now)) { } }
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (byte)i), sent);
        Assert.Equal(0, engine.PacketsDropped);
    }

    [Theory]
    [InlineData(DiagnosticMode.MicroFreeze)]
    [InlineData(DiagnosticMode.SmoothDelay)]
    [InlineData(DiagnosticMode.SoftJitter)]
    public void StrictUnknownTupleAndInboundPass_Unchanged(DiagnosticMode mode)
    {
        using var engine = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(engine, global: false);
        DiagnosticModesTests.Authorize(engine);
        Assert.True(engine.TriggerArmedEffect(mode, 200));
        Assert.False(DiagnosticModesTests.Capture(engine, remotePort: 999));
        Assert.False(DiagnosticModesTests.Capture(engine, outbound: false));
        Assert.True(DiagnosticModesTests.Capture(engine));
        engine.CancelArmedEffect();
        Assert.Equal(0, engine.PacketsDropped); Assert.Equal(3, engine.PacketsPassedThrough);
    }
}
