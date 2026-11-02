
using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public sealed class OutboundEightLifecycleTests
{
    private static long T(int ms) => OutboundPulseScheduler.T(ms);
    private static byte[] Packet(byte protocol, byte id, ushort destinationPort = 443)
    {
        byte[] bytes = new byte[120];
        DiagnosticModesTests.Packet(true, destinationPort, protocol).CopyTo(bytes, 0);
        bytes[2] = 0; bytes[3] = 120;
        if (protocol == 17) { bytes[24] = 0; bytes[25] = 100; }
        bytes[^1] = id; return bytes;
    }

    [Theory, MemberData(nameof(OutboundEightEffectsTests.Modes), MemberType = typeof(OutboundEightEffectsTests))]
    public async Task ConcurrentCaptureReleaseStop_DrainsOnce_NoStaleReleaseOrAddressMutation(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => T(1000))); e.OutboundSeedOverride = 42;
        var sent = new List<byte>();
        var native = new WinDivertNative.Address { Flags = (1u << 17) | (1u << 21), Timestamp = 927341 };
        e.SendOverride = (p, n, a) =>
        {
            Assert.Equal(native.Flags, a.Flags); Assert.Equal(native.Timestamp, a.Timestamp);
            byte[] copy = new byte[n]; Marshal.Copy(p, copy, 0, (int)n);
            byte id = copy[^1]; Assert.Equal(Packet(17, id), copy);
            sent.Add(id); return true;
        };
        Assert.True(e.TriggerArmedEffect(mode, 800));
        void Capture(byte id)
        {
            byte[] bytes = Packet(17, id); var p = Marshal.AllocHGlobal(bytes.Length);
            try { Marshal.Copy(bytes, 0, p, bytes.Length); e.ProcessCapturedPacket(p, (uint)bytes.Length, native, new Random(1)); }
            finally { Marshal.FreeHGlobal(p); }
        }
        Capture(0);
        await Task.WhenAll(Task.Run(() => { for (byte i = 1; i < 100; i++) Capture(i); }),
            Task.Run(() => { for (int i = 0; i < 100; i++) ShapingEffectsTests.Release(e, T(1000)); }), Task.Run(e.Stop));
        e.Stop(); e.FlushThrottleQueue();
        Assert.Equal(100, sent.Count + e.IntentionalBurstDrops); Assert.Equal(sent.Count, sent.Distinct().Count());
        int before = sent.Count; long drops = e.IntentionalBurstDrops;
        ArmedCaptureTests.ModelArm(e);
        e.SendOverride = (_, _, _) => { Assert.Fail("Stale packet survived STOP/restart"); return false; };
        Assert.True(e.TriggerArmedEffect(mode, 800)); ShapingEffectsTests.Release(e, T(1800)); e.Stop();
        Assert.Equal(before, sent.Count); Assert.Equal(drops, e.IntentionalBurstDrops);
    }

    [Theory, MemberData(nameof(OutboundEightEffectsTests.Modes), MemberType = typeof(OutboundEightEffectsTests))]
    public void SaturationCancelsAndDrainsBeforeCurrentPacket_AndDisposeIsIdempotent(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => T(1000))); e.OutboundSeedOverride = 42;
        var ids = new List<byte>(); e.SendOverride = (p, n, _) => { ids.Add(Marshal.ReadByte(p, (int)n - 1)); return true; };
        Assert.True(e.TriggerArmedEffect(mode, 800));
        // Flow cap is exercised even for GE (which intentionally doesn't queue).
        for (int i = 0; i < 129; i++)
        {
            byte[] bytes = Packet(17, (byte)i, destinationPort: (ushort)(10000 + i));
            var p = Marshal.AllocHGlobal(bytes.Length);
            try { Marshal.Copy(bytes, 0, p, bytes.Length); e.ProcessCapturedPacket(p, (uint)bytes.Length, new WinDivertNative.Address { Flags = 1u << 17 }, new Random(1)); }
            finally { Marshal.FreeHGlobal(p); }
        }
        Assert.False(e.IsEffectActive); Assert.True(e.IsRunning); Assert.Contains("fail-open", e.LastShapingFallback);
        Assert.Equal(129, ids.Count + e.IntentionalBurstDrops); Assert.Equal(128, ids[^1]);
        e.Dispose(); e.Dispose(); Assert.False(e.IsCaptureRequested);
    }

    [Fact]
    public void CorrelationAcrossEpochsIsPositiveAndPerFlowStateIsNotShared()
    {
        double sumXY = 0, sumXX = 0; int pairs = 0;
        for (int seed = 1; seed <= 200; seed++)
        {
            var s = new OutboundPulseScheduler(DiagnosticMode.CorrelatedOutboundJitter, T(1000), T(1800), seed);
            var flow = new PulseShaper.Flow(17, "127.0.0.1", 12000, "127.0.0.1", 13000);
            double previous = 0;
            for (int epoch = 0; epoch < 39; epoch++)
            {
                s.Enqueue(new ThrottledPacket { Data = [1], Length = 1, Address = new WinDivertNative.Address { Flags = 1u << 17 } }, flow, T(1000 + epoch * 20));
                double current = s.LastSampleDelayMs - 45;
                if (epoch > 5) { sumXY += previous * current; sumXX += previous * previous; pairs++; }
                previous = current;
            }
            double before = s.LastSampleDelayMs;
            s.Enqueue(new ThrottledPacket { Data = [1], Length = 1, Address = new WinDivertNative.Address { Flags = 1u << 17 } }, flow with { DestinationPort = 14000 }, T(1780));
            Assert.NotEqual(before, s.LastSampleDelayMs);
        }
        Assert.True(pairs > 6000); Assert.InRange(sumXY / sumXX, .78, .91);
    }

    [Theory, MemberData(nameof(OutboundEightEffectsTests.Modes), MemberType = typeof(OutboundEightEffectsTests))]
    public void ValidTcpHasRealSchedulingOrIntentionalLoss_ButGapNeverReordersTcp(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now)); e.OutboundSeedOverride = 42;
        var sent = new List<byte>(); e.SendOverride = (p, n, _) => { sent.Add(Marshal.ReadByte(p, (int)n - 1)); return true; };
        Assert.True(e.TriggerArmedEffect(mode, 800));
        for (byte i = 0; i < 100; i++) ShapingEffectsTests.Capture(e, i, protocol: 6);
        if (mode == DiagnosticMode.GapBasedReordering)
        {
            Assert.Equal(100, sent.Count); Assert.Equal(0, e.PacketsThrottled); Assert.Equal(0, e.IntentionallyReorderedPairs);
        }
        else if (mode == DiagnosticMode.GilbertElliottBurstLoss)
        { Assert.True(e.IntentionalBurstDrops > 0); Assert.Equal(100, sent.Count + e.IntentionalBurstDrops); }
        else { Assert.Empty(sent); Assert.Equal(100, e.PacketsThrottled); }
        now = T(1800); ShapingEffectsTests.Release(e, now);
        Assert.Equal(sent.Order(), sent); Assert.Equal(100, sent.Count + e.IntentionalBurstDrops); Assert.False(e.IsEffectActive);
    }
}
