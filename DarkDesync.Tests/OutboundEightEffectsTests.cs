using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

public sealed class OutboundEightEffectsTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Modes => Enumerable.Range(23, 8).Select(i => new object[] { (DiagnosticMode)i });
    private static long T(int ms) => OutboundPulseScheduler.T(ms);
    private static PulseShaper.Flow F(ushort port = 443, byte protocol = 17) => new(protocol, "127.0.0.1", 40000, "127.0.0.1", port);
    private static ThrottledPacket P(byte id = 0, uint length = 512) => new()
    { Data = [id], Length = length, Address = new WinDivertNative.Address { Flags = 1u << 17 } };
    private static OutboundPulseScheduler S(DiagnosticMode mode, int seed = 42) => new(mode, T(1000), T(1800), seed);

    [Theory, MemberData(nameof(Modes))]
    public void FixedSeedProducesSamePhysicalDecisions_AndEightSignatures(DiagnosticMode mode)
    {
        string Run()
        {
            var s = S(mode); var signature = new List<string>();
            for (int ms = 0; ms <= 800; ms++)
            {
                if (ms < 650 && ms % 5 == 0)
                    signature.Add($"in{ms}:{s.Enqueue(P((byte)(ms / 5)), F(), T(1000 + ms))}");
                while (s.TryDequeue(T(1000 + ms), out var p)) signature.Add($"out{ms}:{p!.Data[0]}");
            }
            Assert.Equal(0, s.Count);
            return string.Join(",", signature);
        }
        var first = Run(); Assert.Equal(first, Run()); output.WriteLine(mode + ": " + first);
    }

    [Fact]
    public void DistinctSchedules_NotAliases()
    {
        var signatures = new HashSet<string>();
        foreach (object[] row in Modes)
        {
            var mode = (DiagnosticMode)row[0]; var s = S(mode); var sent = new List<string>();
            for (byte i = 0; i < 40; i++) sent.Add(s.Enqueue(P(i), F(), T(1000)).ToString());
            for (int ms = 0; ms <= 800; ms++)
                while (s.TryDequeue(T(1000 + ms), out var p)) sent.Add($"{ms}:{p!.Data[0]}");
            Assert.True(signatures.Add(string.Join(",", sent)), mode.ToString());
        }
    }

    [Fact]
    public void SlotsEnforceCadencePacketByteCaps_AndNoLateClosedWindowLeak()
    {
        var s = S(DiagnosticMode.TimeSlotBursting);
        for (byte i = 0; i < 20; i++) s.Enqueue(P(i, 1000), F(), T(1000));
        for (int slot = 0; slot < 3; slot++)
        {
            for (int i = 0; i < 3; i++) Assert.True(s.TryDequeue(T(1000 + slot * 40), out _));
            Assert.False(s.TryDequeue(T(1000 + slot * 40), out _));
            Assert.False(s.TryDequeue(T(1010 + slot * 40), out _));
        }
    }

    [Fact]
    public void SawtoothRateRampsAndResets_TwoPhaseHoldsAndNeverCatchesUpBurst()
    {
        var saw = S(DiagnosticMode.SawtoothPacing);
        for (int i = 0; i < 50; i++) saw.Enqueue(P(length: 64), F(), T(1000));
        Assert.True(saw.TryDequeue(T(1000), out _)); double low = saw.TargetBytesPerSecond;
        Assert.True(saw.TryDequeue(T(1100), out _)); Assert.True(saw.TargetBytesPerSecond > low * 4);
        Assert.True(saw.TryDequeue(T(1120), out _)); Assert.Equal(low, saw.TargetBytesPerSecond);
        var drain = S(DiagnosticMode.TwoPhaseDrain);
        for (int i = 0; i < 20; i++) drain.Enqueue(P(), F(), T(1000));
        Assert.False(drain.TryDequeue(T(1265), out _));
        Assert.True(drain.TryDequeue(T(1267), out _)); Assert.False(drain.TryDequeue(T(1267), out _));
        Assert.True(drain.TryDequeue(T(1700), out _)); Assert.False(drain.TryDequeue(T(1700), out _));
    }

    [Fact]
    public void CorrelatedEpochsAndFlowFifo_HeavyTailHasRealBoundedTail()
    {
        var s = S(DiagnosticMode.CorrelatedOutboundJitter);
        s.Enqueue(P(1), F(), T(1000)); double first = s.LastSampleDelayMs;
        s.Enqueue(P(2), F(), T(1001)); Assert.Equal(first, s.LastSampleDelayMs);
        s.Enqueue(P(3), F(), T(1021)); Assert.NotEqual(first, s.LastSampleDelayMs);
        var ids = new List<byte>();
        for (int ms = 0; ms <= 800; ms++) while (s.TryDequeue(T(1000 + ms), out var p)) ids.Add(p!.Data[0]);
        Assert.Equal(new byte[] { 1, 2, 3 }, ids);
        var heavy = S(DiagnosticMode.HeavyTailJitter); var samples = new List<double>();
        for (int i = 0; i < 10000; i++)
        {
            Assert.Equal(OutboundPulseScheduler.Decision.Held, heavy.Enqueue(P(), F(), T(1000)));
            samples.Add(heavy.LastSampleDelayMs); heavy.Drain().ToArray();
        }
        Assert.All(samples, d => Assert.InRange(d, 4, 140));
        Assert.InRange(samples.Count(d => d > 40), 250, 600);
        Assert.True(samples.Order().ElementAt(9900) > 70);
        output.WriteLine($"Pareto n={samples.Count}; >40ms={samples.Count(d => d > 40)}; p99={samples.Order().ElementAt(9900):F2}ms");
    }

    [Fact]
    public void AdaptiveOccupancyChangesRate_RoundRobinPreventsBusyFlowStarvation()
    {
        var s = S(DiagnosticMode.AdaptiveQueueShaping);
        s.Enqueue(P(), F(), T(1000)); s.TryDequeue(T(1000), out _); double low = s.TargetBytesPerSecond;
        for (int i = 0; i < 40; i++) s.Enqueue(P(1), F(), T(1001));
        s.Enqueue(P(2), F(444), T(1001));
        Assert.True(s.TryDequeue(T(1200), out var sparse)); Assert.Equal(1, sparse!.Data[0]);
        Assert.True(s.TargetBytesPerSecond > low * 3);
        Assert.True(s.TryDequeue(T(1300), out sparse)); Assert.Equal(2, sparse!.Data[0]);
    }

    [Fact]
    public void GapExactIntraFlowUdp_TcpPasses_LoneHeadTimesOut()
    {
        var s = S(DiagnosticMode.GapBasedReordering);
        Assert.Equal(OutboundPulseScheduler.Decision.Pass, s.Enqueue(P(), F(protocol: 6), T(1000)));
        s.Enqueue(P(1), F(), T(1000)); Assert.False(s.TryDequeue(T(1023), out _));
        Assert.True(s.TryDequeue(T(1024), out var p)); Assert.Equal(1, p!.Data[0]);
        s.Enqueue(P(2), F(), T(1025)); s.Enqueue(P(3), F(), T(1026));
        Assert.True(s.TryDequeue(T(1026), out p)); Assert.Equal(3, p!.Data[0]);
        Assert.True(s.TryDequeue(T(1026), out p)); Assert.Equal(2, p!.Data[0]); Assert.Equal(1, s.ReorderedPairs);
        s.Enqueue(P(4), F(), T(1030)); s.Enqueue(P(5), F(444), T(1030));
        Assert.False(s.TryDequeue(T(1031), out _)); // Never reorder across flows.
        Assert.Equal(new byte[] { 4, 5 }, s.Drain().Select(p => p.Data[0]));
    }

    [Fact]
    public void GilbertElliottSeededBurstCorrelation_DifferentConditionalProbabilities_NoQueue()
    {
        var s = S(DiagnosticMode.GilbertElliottBurstLoss);
        int drop = 0, afterDrop = 0, pairs = 0; bool previous = false;
        for (int i = 0; i < 30000; i++)
        {
            bool loss = s.Enqueue(P(), F(), T(1000)) == OutboundPulseScheduler.Decision.Drop;
            if (previous) { pairs++; if (loss) afterDrop++; }
            if (loss) drop++; previous = loss;
        }
        double marginal = drop / 30000d, conditional = afterDrop / (double)pairs;
        Assert.InRange(marginal, .20, .33); Assert.True(conditional > marginal * 1.8);
        Assert.Equal(drop, s.IntentionalDrops); Assert.Equal(0, s.Count);
        output.WriteLine($"GE n=30000; loss={marginal:F4}; P(drop|previous drop)={conditional:F4}; intentional={s.IntentionalDrops}");
    }

    [Theory, MemberData(nameof(Modes))]
    public void BoundsDeadlineStopRestartAndPhysicalPayloadPreservation(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now)); e.OutboundSeedOverride = 42;
        var ids = new List<byte>();
        e.SendOverride = (p, n, a) => { Assert.True(a.Outbound); ids.Add(Marshal.ReadByte(p, (int)n - 1)); return true; };
        Assert.False(e.TriggerArmedEffect(mode, 0)); Assert.False(ShapingEffectsTests.Capture(e, 0));
        Assert.True(e.TriggerArmedEffect(mode, 800));
        for (byte i = 1; i <= 30; i++) ShapingEffectsTests.Capture(e, i, protocol: 17);
        e.Stop(); e.FlushThrottleQueue();
        Assert.Equal(31, ids.Count + e.IntentionalBurstDrops); Assert.Equal(ids.Count, ids.Distinct().Count());
        long released = e.ScheduledPacketsReleased;
        ArmedCaptureTests.ModelArm(e); Assert.True(e.TriggerArmedEffect(mode, 800));
        now = T(1800); ShapingEffectsTests.Release(e, now);
        Assert.False(e.IsEffectActive); Assert.Equal(released, e.ScheduledPacketsReleased);
        Assert.False(ShapingEffectsTests.Capture(e, 99, protocol: 17)); Assert.Equal(99, ids[^1]);
    }

    [Theory, MemberData(nameof(Modes))]
    public void MixedUnsupportedProtocolsPassThroughWithoutCancellingPulse(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        e.SendOverride = (_, _, _) => true; Assert.True(e.TriggerArmedEffect(mode, 800));
        foreach (int kind in new[] { 1, 6, 17, 58 })
        {
            byte[] bytes = new byte[80]; bytes[0] = kind == 1 ? (byte)0x45 : (byte)0x60; bytes[9] = (byte)kind; bytes[6] = (byte)kind;
            IntPtr p = Marshal.AllocHGlobal(80);
            try { Marshal.Copy(bytes, 0, p, 80); Assert.False(e.ProcessCapturedPacket(p, 80, new WinDivertNative.Address { Flags = 1u << 17 }, new Random(1))); }
            finally { Marshal.FreeHGlobal(p); }
        }
        Assert.True(e.IsEffectActive); Assert.Equal(4, e.PacketsPassedThrough); Assert.Equal(0, e.PacketsDropped);
    }

    [Theory, MemberData(nameof(Modes))]
    public void QueueAndFlowLimitsAreBoundedFailOpen(DiagnosticMode mode)
    {
        var s = S(mode);
        if (mode == DiagnosticMode.GilbertElliottBurstLoss) return;
        for (int i = 0; i < 512; i++) Assert.Equal(OutboundPulseScheduler.Decision.Held, s.Enqueue(P(length: 1536), F(), T(1000)));
        Assert.Equal(OutboundPulseScheduler.Decision.Overflow, s.Enqueue(P(), F(), T(1000)));
        Assert.Equal(512, s.Drain().Count()); Assert.Equal(0, s.Bytes);
        for (ushort i = 0; i < 128; i++) Assert.Equal(OutboundPulseScheduler.Decision.Held, s.Enqueue(P(), F(i), T(1000)));
        Assert.Equal(OutboundPulseScheduler.Decision.Overflow, s.Enqueue(P(), F(129), T(1000)));
        Assert.False(s.TryDequeue(T(900), out _) && mode == DiagnosticMode.TwoPhaseDrain);
    }
}
