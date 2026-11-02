using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

public sealed class BufferingVariantsTests(ITestOutputHelper output)
{
    public static TheoryData<DiagnosticMode> Modes => new()
    {
        DiagnosticMode.OutboundFreeze, DiagnosticMode.BurstBuffering, DiagnosticMode.SlottedTransmission,
        DiagnosticMode.SawtoothDelay, DiagnosticMode.BandwidthShaping, DiagnosticMode.AdaptiveFreeze
    };
    private static long T(int ms) => (long)ms * Stopwatch.Frequency / 1000;
    private static bool Release(PacketEngine e, long now) => (bool)typeof(PacketEngine)
        .GetMethod("TryReleaseNext", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(e, [now])!;
    private static ConcurrentQueue<ThrottledPacket> Queue(PacketEngine e) => (ConcurrentQueue<ThrottledPacket>)typeof(PacketEngine)
        .GetField("_throttleQueue", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(e)!;

    [Theory]
    [MemberData(nameof(Modes))]
    public void Pulse_FIFO_Expiry_Stop_AndHiddenFlags(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine();
        Assert.False(e.TriggerArmedEffect(mode, 200));
        ArmedCaptureTests.ModelArm(e);
        long now = T(1000);
        ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        e.ThrottleEnabled = e.ThrottleContinuous = e.IsDropActive = e.IsThrottleActive = true;
        e.ThrottleReleaseDelayMs = 200;
        var sent = new List<int>();
        e.SendOverride = (p, _, _) => { sent.Add((Marshal.ReadByte(p, 22) << 8) | Marshal.ReadByte(p, 23)); return true; };
        Assert.False(DiagnosticModesTests.Capture(e, remotePort: 1));
        Assert.True(e.TriggerArmedEffect(mode, 200));
        Assert.False(e.TriggerArmedEffect(mode, 200));
        for (ushort id = 2; id <= 50; id++)
        {
            now += T(3);
            Assert.True(DiagnosticModesTests.Capture(e, remotePort: id));
            while (Release(e, now)) { }
        }
        now = T(1200);
        Release(e, now);
        Assert.Equal(Enumerable.Range(1, 50), sent);
        Assert.Empty(Queue(e));
        Assert.False(e.IsEffectActive);
        Assert.Equal(DesyncStateMachine.State.Armed, e.StateMachine.Current);
        Assert.True(e.IsRunning);
        Assert.False(DiagnosticModesTests.Capture(e, remotePort: 51));
        Assert.True(e.TriggerArmedEffect(mode, 200));
        DiagnosticModesTests.Capture(e, remotePort: 52);
        e.Stop(); e.FlushThrottleQueue();
        Assert.Equal(Enumerable.Range(1, 52), sent);
        Assert.Equal(0, e.PacketsDropped);
        Assert.False(e.IsCaptureRequested);
    }

    [Fact]
    public void SixModes_HaveDistinctDeterministicReleaseSignatures()
    {
        var signatures = new HashSet<string>();
        foreach (var mode in Modes.Cast<object[]>().Select(row => (DiagnosticMode)row[0]))
        {
            using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
            long now = T(1000);
            ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
            var sends = new List<int>();
            e.SendOverride = (_, _, _) => { sends.Add((int)((now - T(1000)) * 1000 / Stopwatch.Frequency)); return true; };
            Assert.True(e.TriggerArmedEffect(mode, 200));
            for (int ms = 0; ms <= 200; ms++)
            {
                now = T(1000 + ms);
                if (ms is 20 or 40 or 100 or 170)
                    for (int i = 0; i < 8; i++) DiagnosticModesTests.Capture(e);
                while (Release(e, now)) { }
            }
            Assert.Equal(32, sends.Count); Assert.Equal(0, e.PacketsDropped);
            string signature = string.Join(",", sends);
            Assert.True(signatures.Add(signature), "Duplicate schedule: " + mode);
            output.WriteLine($"{mode}: reinjection ms = [{signature}]; FIFO 32/32, dropped=0");
            if (mode == DiagnosticMode.OutboundFreeze) Assert.All(sends, ms => Assert.Equal(200, ms));
            if (mode == DiagnosticMode.BurstBuffering) Assert.Equal(Enumerable.Repeat(80, 16).Concat(Enumerable.Repeat(160, 8)).Concat(Enumerable.Repeat(200, 8)), sends);
            if (mode == DiagnosticMode.SlottedTransmission) Assert.Equal(Enumerable.Repeat(60, 16).Concat(Enumerable.Repeat(120, 8)).Concat(Enumerable.Repeat(180, 8)), sends);
        }
    }

    [Fact]
    public void Sawtooth_RampsThenResetsDeadline_WithoutOvertaking()
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = T(1000);
        ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.SawtoothDelay, 400));
        now = T(1080); DiagnosticModesTests.Capture(e);
        now = T(1159); DiagnosticModesTests.Capture(e);
        now = T(1160); DiagnosticModesTests.Capture(e);
        var packets = Queue(e).ToArray();
        Assert.Equal(T(1140), packets[0].ReleaseAtTicks);
        Assert.Equal(T(1159) + T(159) * 3 / 4, packets[1].ReleaseAtTicks);
        Assert.Equal(T(1160), packets[2].ReleaseAtTicks);
        Assert.True(Release(e, now));
        Assert.False(Release(e, now)); // Reset tooth packet remains behind older head.
        now = packets[1].ReleaseAtTicks;
        Assert.True(Release(e, now)); Assert.True(Release(e, now));
        Assert.Equal(3, e.PacketsPassedThrough);
    }

    [Fact]
    public void Slotted_MissedWindowWaits_ExpiryNeverStarves()
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = T(1000);
        ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.SlottedTransmission, 200));
        now = T(1020); DiagnosticModesTests.Capture(e);
        now = T(1071); Assert.False(Release(e, now));
        now = T(1120); Assert.True(Release(e, now));
        now = T(1199); DiagnosticModesTests.Capture(e);
        now = T(1200); Release(e, now);
        Assert.Empty(Queue(e)); Assert.Equal(2, e.PacketsPassedThrough);
    }

    [Fact]
    public void Bandwidth_IsByteCharged_AndResetOnNextPulse()
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = T(1000);
        ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.BandwidthShaping, 200));
        for (int i = 0; i < 40; i++) DiagnosticModesTests.Capture(e);
        long perPacket = (40L * Stopwatch.Frequency + 16383) / 16384;
        Assert.Equal(Enumerable.Range(1, 40).Select(i => now + i * perPacket), Queue(e).Select(p => p.ReleaseAtTicks));
        e.CancelArmedEffect();
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.BandwidthShaping, 200));
        DiagnosticModesTests.Capture(e);
        Assert.Equal(now + perPacket, Assert.Single(Queue(e)).ReleaseAtTicks);
    }

    [Fact]
    public void Adaptive_ReleasesAtOccupancy32_OrAge90ms()
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = T(1000);
        ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.AdaptiveFreeze, 200));
        for (int i = 0; i < 31; i++) DiagnosticModesTests.Capture(e);
        now = T(1089); Assert.False(Release(e, now));
        DiagnosticModesTests.Capture(e); // Occupancy threshold shortens retention.
        while (Release(e, now)) { }
        Assert.Equal(32, e.PacketsPassedThrough);
        DiagnosticModesTests.Capture(e);
        now = T(1178); Assert.False(Release(e, now));
        now = T(1179); Assert.True(Release(e, now));
        Assert.Equal(33, e.PacketsPassedThrough);
    }

    [Theory]
    [InlineData(DiagnosticMode.BurstBuffering)]
    [InlineData(DiagnosticMode.SlottedTransmission)]
    [InlineData(DiagnosticMode.SawtoothDelay)]
    [InlineData(DiagnosticMode.BandwidthShaping)]
    public void Saturation_CancelsPulse_FullFifoFlush_NoSilentDrop(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = T(1000);
        ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        var sent = new List<int>();
        e.SendOverride = (p, _, _) => { sent.Add((Marshal.ReadByte(p, 22) << 8) | Marshal.ReadByte(p, 23)); return true; };
        Assert.True(e.TriggerArmedEffect(mode, 200));
        for (ushort id = 1; id <= 600; id++) DiagnosticModesTests.Capture(e, remotePort: id);
        Assert.Equal(Enumerable.Range(1, 600), sent);
        Assert.Empty(Queue(e)); Assert.False(e.IsEffectActive);
        Assert.True(e.IsRunning); Assert.Equal(0, e.PacketsDropped);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void StrictScope_UnknownTupleFragmentsInbound_PassUnchanged(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e, global: false);
        DiagnosticModesTests.Authorize(e);
        Assert.True(e.TriggerArmedEffect(mode, 200));
        Assert.False(DiagnosticModesTests.Capture(e, remotePort: 999));
        Assert.False(DiagnosticModesTests.Capture(e, outbound: false));
        Assert.False(DiagnosticModesTests.Capture(e, fragment: true));
        Assert.False(DiagnosticModesTests.Capture(e, protocol: 17));
        Assert.True(DiagnosticModesTests.Capture(e));
        e.CancelArmedEffect();
        Assert.Equal(5, e.PacketsPassedThrough); Assert.Equal(0, e.PacketsDropped);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task ConcurrentReleaseCaptureAndStop_NoDeadlockOrDuplicates(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        Assert.True(e.TriggerArmedEffect(mode, 800));
        var ids = new ConcurrentBag<int>();
        e.SendOverride = (p, _, _) => { ids.Add((Marshal.ReadByte(p, 22) << 8) | Marshal.ReadByte(p, 23)); return true; };
        var producer = Task.Run(() => { for (ushort i = 1; i <= 400; i++) DiagnosticModesTests.Capture(e, remotePort: i); });
        var release = Task.Run(() => { for (int i = 0; i < 400; i++) Release(e, Stopwatch.GetTimestamp()); });
        var stop = Task.Run(e.Stop);
        await Task.WhenAll(producer, release, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(1, 400), ids.Order());
        Assert.Empty(Queue(e)); Assert.Equal(0, e.PacketsDropped);
    }
}
