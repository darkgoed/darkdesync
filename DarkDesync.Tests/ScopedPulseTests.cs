using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public sealed class ScopedPulseTests
{
    public static TheoryData<DiagnosticMode> Modes => new()
    {
        DiagnosticMode.InboundBandwidthShaping, DiagnosticMode.CorrelatedInboundJitter,
        DiagnosticMode.PacketSizeAwareShaping, DiagnosticMode.AsymmetricDuplexShaping,
        DiagnosticMode.ControlledPacketReordering
    };
    private static long T(int ms) => ScopedPulseScheduler.T(ms);
    private static PulseShaper.Flow Flow(bool outbound = false, ushort port = 443) => new(17, outbound ? "out:127.0.0.1" : "in:127.0.0.2", 40000, "127.0.0.1", port);
    private static ThrottledPacket Packet(byte id, int bytes = 512) => new() { Data = [id], Length = (uint)bytes };

    internal static PacketEngine Arm(DiagnosticMode mode, out List<byte> sent)
    {
        var e = DiagnosticModesTests.Engine();
        Assert.True(e.ConfigureScopedProfile(mode));
        ArmedCaptureTests.ModelArm(e, global: false);
        DiagnosticModesTests.Authorize(e);
        e.UdpFlowsOverride = () => [DiagnosticModesTests.Endpoint() with { Protocol = 17 }];
        e.UdpBindingOverride = _ => true;
        e.ExperimentalReorderingConsent = true;
        var ids = new List<byte>(); sent = ids;
        e.SendOverride = (p, n, _) => { ids.Add(Marshal.ReadByte(p, (int)n - 1)); return true; };
        return e;
    }

    internal static bool Capture(PacketEngine e, byte id, bool outbound = false, byte protocol = 17,
        int size = 512, bool nativeLoopback = false, bool impostor = false, ushort port = 443)
    {
        byte[] bytes = new byte[size]; DiagnosticModesTests.Packet(outbound, port, protocol).CopyTo(bytes, 0);
        bytes[2] = (byte)(size >> 8); bytes[3] = (byte)size;
        if (protocol == 17) { bytes[24] = (byte)((size - 20) >> 8); bytes[25] = (byte)(size - 20); }
        bytes[^1] = id;
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, p, size);
            uint flags = (outbound || nativeLoopback ? 1u << 17 : 0) | (nativeLoopback ? 1u << 18 : 0) | (impostor ? 1u << 19 : 0);
            return e.ProcessCapturedPacket(p, (uint)size, new WinDivertNative.Address { Flags = flags }, new Random(1));
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [Fact]
    public void InboundRate_ChargesIpBytes_AggregateBudget_RoundRobinAndNoIdleCreditExplosion()
    {
        var s = new ScopedPulseScheduler(DiagnosticMode.InboundBandwidthShaping, T(1000));
        for (byte i = 0; i < 10; i++) { Assert.True(s.Enqueue(Packet(i), Flow(), T(1000))); Assert.True(s.Enqueue(Packet(i), Flow(port: 444), T(1000))); }
        Assert.False(s.TryDequeue(T(1031), out _));
        Assert.True(s.TryDequeue(T(1032), out var a)); Assert.Equal(0, a!.Data[0]);
        Assert.False(s.TryDequeue(T(1032), out _));
        Assert.True(s.TryDequeue(T(1063), out var b)); Assert.Equal(0, b!.Data[0]);
        int burst = 0; while (s.TryDequeue(T(9000), out _)) burst++;
        Assert.Equal(3, burst); Assert.False(s.TryDequeue(T(8000), out _));
        Assert.Equal(15, s.Drain().Count()); Assert.Equal(0, s.Bytes);
    }

    [Theory]
    [InlineData(DiagnosticMode.InboundBandwidthShaping, 70)]
    [InlineData(DiagnosticMode.AsymmetricDuplexShaping, 140)]
    public void AggregateByteBudget_DoesNotStarveSparseLargeHeadBehindBusyTinyDatagrams(DiagnosticMode mode, int waitMs)
    {
        var s = new ScopedPulseScheduler(mode, T(1000));
        for (byte i = 0; i < 100; i++) s.Enqueue(Packet(i, 64), Flow(), T(1000));
        s.Enqueue(Packet(200, 1000), Flow(port: 444), T(1000));
        Assert.True(s.TryDequeue(T(1010), out var small)); Assert.Equal(0, small!.Data[0]);
        Assert.False(s.TryDequeue(T(1011), out _));
        Assert.True(s.TryDequeue(T(1000 + waitMs), out var sparse)); Assert.Equal(200, sparse!.Data[0]);
    }

    [Fact]
    public void Jitter_IsEpochCorrelated_Seeded_Bounded_AndFifoDespiteFallingDelay()
    {
        var s = new ScopedPulseScheduler(DiagnosticMode.CorrelatedInboundJitter, T(1000), 33);
        var other = new ScopedPulseScheduler(DiagnosticMode.CorrelatedInboundJitter, T(1000), 33);
        var values = new List<double>();
        for (byte i = 0; i < 30; i++)
        {
            long now = T(1000 + i * 5);
            Assert.True(s.Enqueue(Packet(i), Flow(), now)); Assert.True(other.Enqueue(Packet(i), Flow(), now));
            Assert.Equal(other.CorrelatedDelayMs, s.CorrelatedDelayMs);
            Assert.InRange(s.CorrelatedDelayMs, 20, 100); values.Add(s.CorrelatedDelayMs);
            if (i % 4 != 0) Assert.Equal(values[i - 1], values[i]);
        }
        Assert.True(values.Distinct().Count() > 2);
        var ids = new List<byte>();
        for (int ms = 1000; ms < 1300; ms++) while (s.TryDequeue(T(ms), out var p)) ids.Add(p!.Data[0]);
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (byte)i), ids);
        Assert.Empty(s.Drain());
    }

    [Fact]
    public void SizeClasses_UseIpBytes_NotContent_AndKeepPerFlowFifoWithoutCrossFlowBlocking()
    {
        Assert.Equal(0, ScopedPulseScheduler.SizeClass(256)); Assert.Equal(1, ScopedPulseScheduler.SizeClass(257));
        Assert.Equal(1, ScopedPulseScheduler.SizeClass(768)); Assert.Equal(2, ScopedPulseScheduler.SizeClass(769));
        var s = new ScopedPulseScheduler(DiagnosticMode.PacketSizeAwareShaping, T(1000));
        s.Enqueue(Packet(0, 1000), Flow(true), T(1000)); s.Enqueue(Packet(1, 100), Flow(true), T(1000));
        s.Enqueue(Packet(2, 100), Flow(true, 444), T(1000)); s.Enqueue(Packet(3, 100), Flow(true, 444), T(1000));
        Assert.True(s.TryDequeue(T(1000), out var a)); Assert.Equal(0, a!.Data[0]);
        Assert.True(s.TryDequeue(T(1000), out var b)); Assert.Equal(2, b!.Data[0]);
        Assert.False(s.TryDequeue(T(1003), out _));
        Assert.True(s.TryDequeue(T(1004), out var c)); Assert.Equal(3, c!.Data[0]);
        Assert.False(s.TryDequeue(T(1100), out _));
        Assert.True(s.TryDequeue(T(1123), out var d)); Assert.Equal(1, d!.Data[0]);
    }

    [Fact]
    public void Duplex_SeparateRates_OutputGate_AndAtomicFifoDrain()
    {
        var s = new ScopedPulseScheduler(DiagnosticMode.AsymmetricDuplexShaping, T(1000));
        for (byte i = 0; i < 5; i++) { s.Enqueue(Packet(i), Flow(), T(1000)); s.Enqueue(Packet((byte)(10 + i)), Flow(true), T(1000)); }
        Assert.True(s.TryDequeue(T(1016), out var a)); Assert.Equal(10, a!.Data[0]);
        Assert.False(s.TryDequeue(T(1032), out _)); // Output gate closed, inbound lacks credit.
        Assert.True(s.TryDequeue(T(1063), out var b)); Assert.Equal(0, b!.Data[0]);
        Assert.True(s.TryDequeue(T(1063), out var c)); Assert.Equal(11, c!.Data[0]);
        var drain = s.Drain().Select(p => p.Data[0]).ToArray();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 12, 13, 14 }, drain);
        Assert.False(s.TryDequeue(T(2000), out _));
    }

    [Fact]
    public void Reordering_AdjacentPairsOnly_SingletonTimeout_AndStopCancelsPendingSwaps()
    {
        var s = new ScopedPulseScheduler(DiagnosticMode.ControlledPacketReordering, T(1000));
        for (byte i = 0; i < 5; i++) s.Enqueue(Packet(i), Flow(true), T(1000));
        var ids = new List<byte>(); while (s.TryDequeue(T(1001), out var p)) ids.Add(p!.Data[0]);
        Assert.Equal(new byte[] { 1, 0, 3, 2 }, ids); Assert.Equal(2, s.ReorderedPairs);
        Assert.False(s.TryDequeue(T(1019), out _)); Assert.True(s.TryDequeue(T(1020), out var last)); Assert.Equal(4, last!.Data[0]);
        s.Enqueue(Packet(5), Flow(true), T(1030)); s.Enqueue(Packet(6), Flow(true), T(1030));
        Assert.Equal(new byte[] { 5, 6 }, s.Drain().Select(p => p.Data[0]));
        Assert.False(s.TryDequeue(T(1100), out _)); Assert.Equal(2, s.ReorderedPairs);
    }

    [Theory, MemberData(nameof(Modes))]
    public void QueuesBoundedByPacketsBytesAndFlowHistory(DiagnosticMode mode)
    {
        var s = new ScopedPulseScheduler(mode, T(1000));
        Assert.False(s.Enqueue(Packet(0, 1537), Flow(), T(1000)));
        for (int i = 0; i < 512; i++) Assert.True(s.Enqueue(Packet((byte)i, 1536), Flow(), T(1000)));
        Assert.Equal(512 * 1536, s.Bytes); Assert.False(s.Enqueue(Packet(0), Flow(), T(1000)));
        Assert.Equal(512, s.Drain().Count());
        for (ushort i = 1; i <= 128; i++) Assert.True(s.Enqueue(Packet(0), Flow(port: i), T(1000)));
        Assert.False(s.Enqueue(Packet(0), Flow(port: 129), T(1000)));
    }

    [Theory, MemberData(nameof(Modes))]
    public void Engine_StartExpiryCancelStop_NoReplayOrDuplicates_AndGlobalRefused(DiagnosticMode mode)
    {
        using var e = Arm(mode, out var ids);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        bool outbound = mode is DiagnosticMode.PacketSizeAwareShaping or DiagnosticMode.ControlledPacketReordering;
        bool loopback = mode == DiagnosticMode.ControlledPacketReordering;
        Assert.False(Capture(e, 0, outbound, nativeLoopback: loopback));
        Assert.True(e.TriggerArmedEffect(mode, 200)); Assert.False(e.TriggerArmedEffect(mode, 200));
        for (byte i = 1; i < 10; i++) Assert.True(Capture(e, i, outbound, nativeLoopback: loopback));
        now = T(1200); ShapingEffectsTests.Release(e, now);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (byte)i), ids);
        Assert.Equal(DesyncStateMachine.State.Armed, e.StateMachine.Current); Assert.True(e.IsRunning);
        Assert.True(e.TriggerArmedEffect(mode, 200)); Capture(e, 10, outbound, nativeLoopback: loopback); e.CancelArmedEffect();
        Assert.True(e.TriggerArmedEffect(mode, 200)); Capture(e, 11, outbound, nativeLoopback: loopback); e.Stop();
        ShapingEffectsTests.Release(e, now + T(500)); e.FlushThrottleQueue();
        Assert.Equal(Enumerable.Range(0, 12).Select(i => (byte)i), ids); Assert.Equal(0, e.PacketsDropped);
        ArmedCaptureTests.ModelArm(e); Assert.False(e.TriggerArmedEffect(mode, 200));
    }

    [Theory, MemberData(nameof(Modes))]
    public void Engine_FailsOpenOnSaturation_WithoutIntentionalLoss(DiagnosticMode mode)
    {
        using var e = Arm(mode, out var ids);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        Assert.True(e.TriggerArmedEffect(mode, 800));
        bool outbound = mode is DiagnosticMode.PacketSizeAwareShaping or DiagnosticMode.ControlledPacketReordering;
        for (int i = 0; i < 600; i++) Capture(e, (byte)i, outbound, nativeLoopback: mode == DiagnosticMode.ControlledPacketReordering);
        Assert.False(e.IsEffectActive); Assert.Equal(600, ids.Count); Assert.Equal(0, e.PacketsDropped);
        Assert.Equal(Enumerable.Range(0, 600).Select(i => (byte)i), ids);
    }

    [Fact]
    public void Engine_RealInboundAndLogicalLoopback_KeepNativeReinjectionDirection_OutboundUntouched()
    {
        using var e = Arm(DiagnosticMode.InboundBandwidthShaping, out var ids);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        var nativeFlags = new List<bool>(); e.SendOverride = (_, _, a) => { nativeFlags.Add(a.Outbound); return true; };
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.InboundBandwidthShaping, 200));
        Assert.True(Capture(e, 0)); Assert.True(Capture(e, 1, nativeLoopback: true)); Assert.False(Capture(e, 2, outbound: true));
        e.CancelArmedEffect(); Assert.Equal(new[] { true, false, true }, nativeFlags);
        Assert.Equal(0, e.PacketsDropped);
    }

    [Fact]
    public void UnknownOwnershipAmbiguousLoopbackImpostorAndDeadPid_CancelFailOpen()
    {
        using var e = Arm(DiagnosticMode.AsymmetricDuplexShaping, out var ids);
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.AsymmetricDuplexShaping, 800));
        Assert.True(Capture(e, 0)); Assert.False(Capture(e, 1, port: 444)); Assert.False(e.IsEffectActive);
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.AsymmetricDuplexShaping, 800));
        Assert.False(Capture(e, 2, impostor: true)); Assert.False(e.IsEffectActive);
        e.EndpointsOverride = _ => [DiagnosticModesTests.Endpoint(), new(6, 443, 40000, DiagnosticModesTests.Endpoint().RemoteAddr, DiagnosticModesTests.Endpoint().LocalAddr)];
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.AsymmetricDuplexShaping, 800));
        Assert.False(Capture(e, 3, protocol: 6, nativeLoopback: true)); Assert.False(e.IsEffectActive);
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.AsymmetricDuplexShaping, 800));
        e.TargetAliveOverride = () => false; Assert.False(Capture(e, 4));
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, ids); Assert.Equal(0, e.PacketsDropped);
    }

    [Fact]
    public void Reordering_RequiresConsent_UDPOnly_AndNativeLoopbackOnly()
    {
        using var e = Arm(DiagnosticMode.ControlledPacketReordering, out var ids);
        e.ExperimentalReorderingConsent = false; Assert.False(e.TriggerArmedEffect(DiagnosticMode.ControlledPacketReordering, 800));
        e.ExperimentalReorderingConsent = true; Assert.True(e.TriggerArmedEffect(DiagnosticMode.ControlledPacketReordering, 800));
        Assert.False(Capture(e, 0, true, protocol: 6, nativeLoopback: true));
        Assert.False(Capture(e, 1, true)); // Native non-loopback never reorders, even with synthetic 127/8 headers.
        Assert.True(Capture(e, 2, true, nativeLoopback: true)); Assert.True(Capture(e, 3, true, nativeLoopback: true));
        ShapingEffectsTests.Release(e, System.Diagnostics.Stopwatch.GetTimestamp());
        e.Stop(); Assert.Equal(new byte[] { 0, 1, 3, 2 }, ids); Assert.Equal(1, e.IntentionallyReorderedPairs);
        Assert.Equal(0, e.PacketsDropped);
    }

    [Theory]
    [InlineData(DiagnosticMode.InboundBandwidthShaping)]
    [InlineData(DiagnosticMode.CorrelatedInboundJitter)]
    [InlineData(DiagnosticMode.AsymmetricDuplexShaping)]
    public void TcpFixtures_KeepFifoAndOriginalBytesInBothDirections(DiagnosticMode mode)
    {
        using var e = Arm(mode, out var ids);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        Assert.True(e.TriggerArmedEffect(mode, 200));
        for (byte i = 0; i < 12; i++) Assert.True(Capture(e, i, protocol: 6));
        if (mode == DiagnosticMode.AsymmetricDuplexShaping)
            for (byte i = 12; i < 24; i++) Assert.True(Capture(e, i, outbound: true, protocol: 6));
        else Assert.False(Capture(e, 99, outbound: true, protocol: 6));
        now = T(1200); ShapingEffectsTests.Release(e, now);
        Assert.Equal(Enumerable.Range(0, mode == DiagnosticMode.AsymmetricDuplexShaping ? 24 : 12).Select(i => (byte)i), ids.Where(i => i != 99));
        Assert.Equal(0, e.PacketsDropped);
    }

    [Fact]
    public void RevokingExperimentalConsent_FlushesBeforeNewerDatagrams_AndCannotRearm()
    {
        using var e = Arm(DiagnosticMode.ControlledPacketReordering, out var ids);
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.ControlledPacketReordering, 800));
        Assert.True(Capture(e, 0, true, nativeLoopback: true)); Assert.True(Capture(e, 1, true, nativeLoopback: true));
        e.ExperimentalReorderingConsent = false;
        Assert.False(Capture(e, 2, true, nativeLoopback: true));
        Assert.Equal(new byte[] { 0, 1, 2 }, ids); Assert.False(e.IsEffectActive);
        Assert.False(e.TriggerArmedEffect(DiagnosticMode.ControlledPacketReordering, 800));
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task TimerWatchdog_FlushesWithoutPacketsOrReleaseWorker(DiagnosticMode mode)
    {
        using var e = Arm(mode, out var ids);
        Assert.True(e.TriggerArmedEffect(mode, 50));
        bool outbound = mode is DiagnosticMode.PacketSizeAwareShaping or DiagnosticMode.ControlledPacketReordering;
        Assert.True(Capture(e, 0, outbound, nativeLoopback: mode == DiagnosticMode.ControlledPacketReordering));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (e.IsEffectActive && clock.ElapsedMilliseconds < 3000) await Task.Delay(5);
        Assert.False(e.IsEffectActive); Assert.Equal(new byte[] { 0 }, ids); Assert.True(e.IsRunning);
        Assert.Equal(DesyncStateMachine.State.Armed, e.StateMachine.Current);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task ReinjectionFailure_IsReportedAsFailure_NotIntentionalDrop(DiagnosticMode mode)
    {
        using var e = Arm(mode, out _);
        Assert.True(e.TriggerArmedEffect(mode, 800));
        bool outbound = mode is DiagnosticMode.PacketSizeAwareShaping or DiagnosticMode.ControlledPacketReordering;
        Assert.True(Capture(e, 0, outbound, nativeLoopback: mode == DiagnosticMode.ControlledPacketReordering));
        e.SendOverride = (_, _, _) => false;
        e.CancelArmedEffect();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (e.IsRunning && clock.ElapsedMilliseconds < 3000) await Task.Delay(5);
        Assert.False(e.IsRunning); Assert.Equal(1, e.PacketsDropped); Assert.Equal(1, e.PacketsFailedToSend);
        Assert.Equal(0, e.IntentionallyReorderedPairs);
    }

    [Theory, MemberData(nameof(Modes))]
    public async Task CaptureReleaseStop_AreSerializedAndNoWorkSurvivesStop(DiagnosticMode mode)
    {
        using var e = Arm(mode, out var ids);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        Assert.True(e.TriggerArmedEffect(mode, 800));
        bool outbound = mode is DiagnosticMode.PacketSizeAwareShaping or DiagnosticMode.ControlledPacketReordering;
        await Task.WhenAll(Task.Run(() => { for (int i = 0; i < 200; i++) Capture(e, (byte)i, outbound, nativeLoopback: mode == DiagnosticMode.ControlledPacketReordering); }),
            Task.Run(() => { for (int i = 0; i < 200; i++) ShapingEffectsTests.Release(e, now); }), Task.Run(e.Stop)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (byte)i), ids.Order());
        int total = ids.Count; ShapingEffectsTests.Release(e, now + T(10000)); Assert.Equal(total, ids.Count); Assert.Equal(0, e.PacketsDropped);
    }
}
