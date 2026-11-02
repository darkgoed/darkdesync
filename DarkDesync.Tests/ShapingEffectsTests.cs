using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

public sealed class ShapingEffectsTests(ITestOutputHelper output)
{
    public static TheoryData<DiagnosticMode> NewModes => new()
    {
        DiagnosticMode.HoldAndDrainShaping, DiagnosticMode.RateStepShaping,
        DiagnosticMode.DualTokenBucket, DiagnosticMode.FlowAwareShaping
    };
    public static TheoryData<DiagnosticMode> SixModes => new()
    {
        DiagnosticMode.OutboundFreeze, DiagnosticMode.BandwidthShaping,
        DiagnosticMode.HoldAndDrainShaping, DiagnosticMode.RateStepShaping,
        DiagnosticMode.DualTokenBucket, DiagnosticMode.FlowAwareShaping
    };
    internal static long T(int ms) => (long)ms * Stopwatch.Frequency / 1000;
    internal static bool Release(PacketEngine engine, long now) => (bool)typeof(PacketEngine)
        .GetMethod("TryReleaseNext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [now])!;
    internal static bool Capture(PacketEngine engine, byte id, int size = 512, ushort port = 443, byte protocol = 6)
    {
        byte[] bytes = new byte[size];
        DiagnosticModesTests.Packet(true, port, protocol).CopyTo(bytes, 0);
        bytes[2] = (byte)(size >> 8); bytes[3] = (byte)size;
        if (protocol == 17) { bytes[24] = (byte)((size - 20) >> 8); bytes[25] = (byte)(size - 20); }
        bytes[^1] = id;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(bytes, 0, buffer, size);
            return engine.ProcessCapturedPacket(buffer, (uint)size, new WinDivertNative.Address { Flags = 1u << 17 }, new Random(1));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static PulseShaper New(DiagnosticMode mode) => new(mode, T(1000), T(1200));
    private static ThrottledPacket Packet(int length = 512, byte id = 0) => new() { Length = (uint)length, Data = [id] };
    private static PulseShaper.Flow Flow(ushort port = 443, byte protocol = 6, string source = "127.0.0.1", ushort local = 40000) =>
        new(protocol, source, local, "127.0.0.2", port);

    [Fact]
    public void HoldDrain_HoldsFortyPercent_ThenPaces_WithoutIdleBurst()
    {
        var s = New(DiagnosticMode.HoldAndDrainShaping);
        for (byte id = 0; id < 10; id++) Assert.True(s.Enqueue(Packet(id: id), default));
        Assert.False(s.TryDequeue(T(1079), out _));
        Assert.False(s.TryDequeue(T(1080), out _));
        Assert.False(s.TryDequeue(T(1095), out _));
        Assert.True(s.TryDequeue(T(1096), out var first)); Assert.Equal(0, first!.Data[0]);
        Assert.False(s.TryDequeue(T(1096), out _));
        Assert.True(s.TryDequeue(T(1112), out var second)); Assert.Equal(1, second!.Data[0]);
        Assert.Equal(Enumerable.Range(2, 8).Select(i => (byte)i), s.Drain().Select(p => p.Data[0]));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void RateStep_IntegratesAcrossBoundary_NotPerPacketDeadlines()
    {
        var s = New(DiagnosticMode.RateStepShaping);
        for (int i = 0; i < 20; i++) Assert.True(s.Enqueue(Packet(256), default));
        Assert.False(s.TryDequeue(T(1062), out _));
        Assert.True(s.TryDequeue(T(1063), out _));
        Assert.False(s.TryDequeue(T(1100), out _));
        Assert.True(s.TryDequeue(T(1104), out _)); // 409.6 + 131.072 bytes, minus first 256.
        Assert.False(s.TryDequeue(T(1104), out _));
        Assert.True(s.TryDequeue(T(1111), out _));
        Assert.False(s.TryDequeue(T(1000), out _)); // Regression cannot mint credit.
    }

    [Fact]
    public void DualBucket_ChargesFullIpBytes_EnforcesBothEnvelopes_AndCapsIdleTokens()
    {
        var s = New(DiagnosticMode.DualTokenBucket);
        for (int i = 0; i < 50; i++) Assert.True(s.Enqueue(Packet(256), default));
        var sent = new List<int>();
        for (int ms = 0; ms < 200; ms++)
            while (s.TryDequeue(T(1000 + ms), out _)) sent.Add(ms);
        Assert.NotEmpty(sent);
        for (int i = 0; i < sent.Count; i++)
        {
            Assert.True((i + 1) * 256 <= 1024 + sent[i] * 8192d / 1000 + 0.001);
            Assert.True((i + 1) * 256 <= sent[i] * 32768d / 1000 + 0.001);
            for (int j = 0; j <= i; j++)
            {
                Assert.True((i - j + 1) * 256 <= 2048 + (sent[i] - sent[j]) * 8192d / 1000 + 0.001);
                Assert.True((i - j + 1) * 256 <= 1536 + (sent[i] - sent[j]) * 32768d / 1000 + 0.001);
            }
        }
        int late = 0;
        while (s.TryDequeue(T(10000), out _)) late++;
        Assert.Equal(6, late); // Peak cap, not nine seconds of unlimited catch-up.
        Assert.False(s.TryDequeue(T(9000), out _));
    }

    [Fact]
    public void FlowAware_IndependentBudgets_RoundRobin_FifoAndCompleteTupleIdentity()
    {
        var s = New(DiagnosticMode.FlowAwareShaping);
        var flows = new[] { Flow(), Flow(444), Flow(protocol: 17), Flow(source: "127.0.0.3"), Flow(local: 40001) };
        foreach (var f in flows)
            for (byte id = 0; id < 8; id++) Assert.True(s.Enqueue(Packet(id: id), f));
        Assert.Equal(5, s.FlowCount);
        for (int round = 0; round < 3; round++)
            foreach (var _ in flows)
            {
                Assert.True(s.TryDequeue(T(1200), out var packet));
                Assert.Equal(round, packet!.Data[0]);
            }
        Assert.False(s.TryDequeue(T(1200), out _)); // 1536 cap independently for each flow.
        Assert.Equal(25, s.Drain().Count());
        Assert.Equal(0, s.FlowCount);
    }

    [Theory]
    [MemberData(nameof(NewModes))]
    public void BoundedPacketsBytesAndFlowHistory(DiagnosticMode mode)
    {
        var s = New(mode);
        Assert.False(s.Enqueue(Packet(1537), default));
        for (int i = 0; i < 512; i++) Assert.True(s.Enqueue(Packet(1536), Flow()));
        Assert.False(s.Enqueue(Packet(), Flow()));
        Assert.Equal(512, s.Drain().Count());
        if (mode != DiagnosticMode.FlowAwareShaping) return;
        for (ushort i = 1; i <= 128; i++) Assert.True(s.Enqueue(Packet(), Flow(i)));
        Assert.False(s.Enqueue(Packet(), Flow(129)));
        Assert.Equal(128, s.FlowCount);
    }

    [Theory]
    [MemberData(nameof(SixModes))]
    public void StartHasNoEffect_ExpiryFlushesAndRemainsArmed_CancelStopAndEpochReset(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine();
        Assert.False(e.TriggerArmedEffect(mode, 200));
        ArmedCaptureTests.ModelArm(e);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        var ids = new List<byte>();
        e.SendOverride = (p, n, _) => { ids.Add(Marshal.ReadByte(p, (int)n - 1)); return true; };
        e.ThrottleEnabled = e.ThrottleContinuous = e.IsDropActive = e.IsThrottleActive = true;
        Assert.False(Capture(e, 0));
        Assert.True(e.TriggerArmedEffect(mode, 200)); Assert.False(e.TriggerArmedEffect(mode, 200));
        for (byte id = 1; id <= 20; id++) Assert.True(Capture(e, id));
        now = T(1200); Release(e, now);
        Assert.Equal(Enumerable.Range(0, 21).Select(i => (byte)i), ids);
        Assert.True(e.IsRunning); Assert.False(e.IsEffectActive);
        Assert.Equal(DesyncStateMachine.State.Armed, e.StateMachine.Current);
        Assert.False(Capture(e, 21));
        Assert.True(e.TriggerArmedEffect(mode, 200)); Capture(e, 22); e.CancelArmedEffect();
        Assert.True(e.TriggerArmedEffect(mode, 200)); Capture(e, 23); e.Stop(); e.FlushThrottleQueue();
        Assert.Equal(Enumerable.Range(0, 24).Select(i => (byte)i), ids);
        Assert.Equal(0, e.PacketsDropped); Assert.False(e.IsCaptureRequested);
        ArmedCaptureTests.ModelArm(e);
        e.SendOverride = (p, n, _) => { ids.Add(Marshal.ReadByte(p, (int)n - 1)); return true; };
        Assert.True(e.TriggerArmedEffect(mode, 200));
        Capture(e, 24); e.Stop(); Assert.Equal(24, ids[^1]);
    }

    [Theory]
    [MemberData(nameof(NewModes))]
    public void StrictScopeRejectsUnknownFragmentsInbound_AndSaturationFailsOpen(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e, global: false);
        DiagnosticModesTests.Authorize(e);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        Assert.True(e.TriggerArmedEffect(mode, 200));
        Assert.False(DiagnosticModesTests.Capture(e, remotePort: 444));
        Assert.False(DiagnosticModesTests.Capture(e, outbound: false));
        Assert.False(DiagnosticModesTests.Capture(e, fragment: true));
        Assert.False(DiagnosticModesTests.Capture(e, protocol: 17));
        for (int i = 0; i < 600; i++) Capture(e, (byte)i);
        Assert.False(e.IsEffectActive); Assert.True(e.IsRunning);
        Assert.Equal(604, e.PacketsPassedThrough); Assert.Equal(0, e.PacketsDropped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlowUnknownOrOversize_CancelsAndDrainsInsteadOfRetaining(bool oversize)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.FlowAwareShaping, 200)); Capture(e, 1);
        if (oversize) Assert.False(Capture(e, 2, 2000));
        else Assert.False(DiagnosticModesTests.Capture(e, fragment: true));
        Assert.False(e.IsEffectActive); Assert.Equal(2, e.PacketsPassedThrough); Assert.Equal(0, e.PacketsDropped);
    }

    [Fact]
    public void SixAlgorithms_HaveDistinctMeasuredSchedules()
    {
        var signatures = new HashSet<string>();
        foreach (DiagnosticMode mode in SixModes)
        {
            using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
            long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
            var sent = new List<int>();
            e.SendOverride = (_, _, _) => { sent.Add((int)((now - T(1000)) * 1000 / Stopwatch.Frequency)); return true; };
            Assert.True(e.TriggerArmedEffect(mode, 200));
            for (int ms = 0; ms <= 200; ms++)
            {
                now = T(1000 + ms);
                if (ms == 0) for (byte id = 0; id < 24; id++) Capture(e, id, port: (ushort)(443 + id % 2));
                while (Release(e, now)) { }
            }
            Assert.Equal(24, sent.Count);
            string signature = string.Join(",", sent);
            Assert.True(signatures.Add(signature), "Duplicate algorithm signature: " + mode);
            output.WriteLine($"{mode}: release ms=[{signature}]; 24/24, application-dropped=0");
        }
    }

    [Theory]
    [MemberData(nameof(NewModes))]
    public async Task ConcurrentCaptureReleaseStop_Bounded_NoDeadlockOrDuplicates(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        var ids = new System.Collections.Concurrent.ConcurrentBag<byte>();
        e.SendOverride = (p, n, _) => { ids.Add(Marshal.ReadByte(p, (int)n - 1)); return true; };
        Assert.True(e.TriggerArmedEffect(mode, 800));
        await Task.WhenAll(Task.Run(() => { for (byte i = 0; i < 200; i++) Capture(e, i); }),
            Task.Run(() => { for (int i = 0; i < 400; i++) Release(e, now); }), Task.Run(e.Stop)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (byte)i), ids.Order());
        Assert.Equal(0, e.PacketsDropped);
    }
}
