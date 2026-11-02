using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public sealed class NetworkTechniquesTests
{
    public static IEnumerable<object[]> Modes => Enumerable.Range(31, 5).Select(x => new object[] { (DiagnosticMode)x });
    internal static byte[] Packet(bool v6 = false, byte protocol = 17, byte traffic = 0, int payload = 24, byte flags = 0x10, ushort sourcePort = 40000, ushort destinationPort = 443)
    {
        int ip = v6 ? 40 : 20, transport = protocol == 6 ? 20 : 8;
        byte[] p = new byte[ip + transport + payload];
        if (v6) { p[0] = (byte)(0x60 | (traffic >> 4)); p[1] = (byte)((traffic << 4) | 0xb); p[2] = 0x19; p[3] = 0x29; BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)(p.Length - 40)); p[6] = protocol; p[7] = 64; p[23] = p[39] = 1; }
        else { p[0] = 0x45; p[1] = traffic; BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)p.Length); p[4] = 0x12; p[5] = 0x34; p[6] = 0x40; p[8] = 64; p[9] = protocol; p[12] = p[16] = 127; p[15] = p[19] = 1; }
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(ip), sourcePort); BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(ip + 2), destinationPort);
        if (protocol == 17) BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(ip + 4), (ushort)(p.Length - ip));
        else { p[ip + 4] = 0x11; p[ip + 8] = 0x22; p[ip + 12] = 0x50; p[ip + 13] = flags; p[ip + 14] = 0x40; }
        for (int i = ip + transport; i < p.Length; i++) p[i] = (byte)(i * 17);
        // Independent reference checksum producer, not the production repairer.
        RepairReference(p, v6, protocol);
        return p;
    }
    internal static ushort Checksum(IEnumerable<byte> input)
    {
        byte[] bytes = input.ToArray(); ulong sum = 0;
        for (int i = 0; i < bytes.Length; i += 2) sum += (uint)(bytes[i] * 256 + (i + 1 < bytes.Length ? bytes[i + 1] : 0));
        while (sum > 65535) sum = (sum & 65535) + (sum >> 16);
        return (ushort)(65535 - sum);
    }
    internal static byte[] Pseudo(byte[] p, bool v6, byte protocol)
    {
        int ip = v6 ? 40 : (p[0] & 15) * 4;
        byte[] suffix = v6 ? new byte[] { 0, 0, (byte)((p.Length - ip) >> 8), (byte)(p.Length - ip), 0, 0, 0, protocol }
            : new byte[] { 0, protocol, (byte)((p.Length - ip) >> 8), (byte)(p.Length - ip) };
        return p.Skip(v6 ? 8 : 12).Take(v6 ? 32 : 8).Concat(suffix).Concat(p.Skip(ip)).ToArray();
    }
    internal static void RepairReference(byte[] p, bool v6, byte protocol)
    {
        int ip = v6 ? 40 : (p[0] & 15) * 4, c = ip + (protocol == 6 ? 16 : 6);
        p[c] = p[c + 1] = 0; ushort check = Checksum(Pseudo(p, v6, protocol)); if (protocol == 17 && check == 0) check = 65535;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(c), check);
        if (!v6) { p[10] = p[11] = 0; BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(10), Checksum(p.Take(ip))); }
    }
    internal static void AssertChecksums(byte[] p)
    {
        bool v6 = p[0] >> 4 == 6; byte protocol = p[v6 ? 6 : 9]; int ip = v6 ? 40 : (p[0] & 15) * 4;
        if (!v6) Assert.Equal(0, Checksum(p.Take(ip)));
        Assert.Equal(0, Checksum(Pseudo(p, v6, protocol)));
    }
    internal static bool Capture(PacketEngine e, byte[] bytes, bool outbound = true, bool impostor = false)
    {
        IntPtr p = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, p, bytes.Length);
            uint flags = (outbound ? 1u << 17 : 0) | (bytes[0] >> 4 == 6 ? 1u << 20 : 0) | (impostor ? 1u << 19 : 0);
            return e.ProcessCapturedPacket(p, (uint)bytes.Length, new WinDivertNative.Address { Flags = flags, Timestamp = 918273 }, new Random(42));
        }
        finally { Marshal.FreeHGlobal(p); }
    }
    [Theory]
    [InlineData(false, 17)] [InlineData(true, 17)] [InlineData(false, 6)] [InlineData(true, 6)]
    public void HeaderMarksPreservePayloadFlowLabelEcnAndRepairChecksums(bool v6, byte protocol)
    {
        foreach (byte ecn in new byte[] { 0, 1, 2, 3 })
        foreach (var mode in new[] { DiagnosticMode.EcnCongestionMarking, DiagnosticMode.DscpTrafficMarking })
        {
            byte[] original = Packet(v6, protocol, (byte)(20 | ecn), payload: 31), before = original.ToArray();
            bool changed = TechniquePackets.TryMark(original, mode, 46, out var result);
            Assert.Equal(before, original);
            bool expected = mode == DiagnosticMode.DscpTrafficMarking || ecn is 1 or 2;
            Assert.Equal(expected, changed); if (!changed) { Assert.Null(result); continue; }
            Assert.True(TechniquePackets.TryParse(result!, out var l)); AssertChecksums(result!);
            Assert.Equal(mode == DiagnosticMode.DscpTrafficMarking ? (byte)((46 << 2) | ecn) : (byte)(20 | 3), TechniquePackets.TrafficClass(result!, l));
            Assert.Equal(original.Skip(l.Transport + l.HeaderLength), result!.Skip(l.Transport + l.HeaderLength));
            if (v6) { Assert.Equal(original[1] & 15, result![1] & 15); Assert.Equal(original.Skip(2).Take(38), result.Skip(2).Take(38)); }
            Assert.Equal(TechniquePackets.Flow(original, l), TechniquePackets.Flow(result, l));
        }
    }
    [Fact]
    public void UnsupportedFragmentsExtensionsIcmpAndOversizePassWithoutMutation()
    {
        var fragment = Packet(); fragment[6] = 0x20;
        var reserved = Packet(); reserved[6] = 0x80;
        var ext = Packet(true); ext[6] = 0;
        var icmp = Packet(); icmp[9] = 1;
        foreach (byte[] p in new[] { fragment, reserved, ext, icmp, Packet(payload: 5000) })
        { Assert.False(TechniquePackets.TryMark(p, DiagnosticMode.DscpTrafficMarking, 46, out var modified)); Assert.Null(modified); }
        Assert.False(TechniquePackets.TryMark(Packet(), DiagnosticMode.DscpTrafficMarking, 64, out _));
    }
    [Theory, MemberData(nameof(Modes))]
    public void StartAndZeroDurationNeverModify_StopRestartHasNoStalePacket(DiagnosticMode mode)
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = AckPulseQueue.T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        var sent = new List<byte[]>(); e.SendOverride = (p, n, _) => { byte[] b = new byte[n]; Marshal.Copy(p, b, 0, (int)n); sent.Add(b); return true; };
        byte[] packet = Packet(traffic: 2); Capture(e, packet); Assert.Single(sent); Assert.Equal(packet, sent[0]);
        Assert.False(e.TriggerArmedEffect(mode, 0)); Assert.True(e.TriggerArmedEffect(mode, 800)); Capture(e, packet);
        e.Stop(); int before = sent.Count; e.FlushThrottleQueue(); Assert.Equal(before, sent.Count);
        Assert.False(e.IsEffectActive); Assert.False(e.MtuLabWork is { IsCompleted: false });
        ArmedCaptureTests.ModelArm(e); e.SendOverride = (p, n, _) => { byte[] b = new byte[n]; Marshal.Copy(p, b, 0, (int)n); sent.Add(b); return true; };
        Capture(e, packet); Assert.Equal(before + 1, sent.Count); Assert.Equal(packet, sent[^1]);
    }
    [Fact]
    public void DuplicationIsUdpOnlyBoundedAndNeverCopiesImpostorsOrAfterExpiry()
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = AckPulseQueue.T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        var copies = new List<byte[]>(); e.SendOverride = (p, n, a) => { Assert.Equal(918273, a.Timestamp); var b = new byte[n]; Marshal.Copy(p, b, 0, (int)n); copies.Add(b); return true; };
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.PacketDuplication, 800));
        for (int i = 0; i < 40; i++) Capture(e, Packet(i % 2 == 0));
        Assert.Equal(16, e.DuplicateCopiesSent); Assert.Equal(56, copies.Count);
        int before = copies.Count; Capture(e, Packet(protocol: 6)); Capture(e, Packet(), impostor: true); Capture(e, Packet(), outbound: false);
        Assert.Equal(before + 3, copies.Count); Assert.Equal(16, e.DuplicateCopiesSent);
        now = AckPulseQueue.T(1800); Capture(e, Packet()); Assert.Equal(before + 4, copies.Count); Assert.Equal(16, e.DuplicateCopiesSent);
        Assert.Equal(0, e.PacketsDropped);
    }
    [Fact]
    public void AckPacingPureOnly_FifoPerFlow_DataDrainsPendingAndOverflowFailOpen()
    {
        using var e = DiagnosticModesTests.Engine(); ArmedCaptureTests.ModelArm(e);
        long now = AckPulseQueue.T(1000); ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
        var sent = new List<byte[]>(); e.SendOverride = (p, n, _) => { var b = new byte[n]; Marshal.Copy(p, b, 0, (int)n); sent.Add(b); return true; };
        Assert.True(e.TriggerArmedEffect(DiagnosticMode.TcpAckPacing, 800));
        byte[] pure = Packet(protocol: 6, payload: 0);
        Assert.True(Capture(e, pure)); Assert.Empty(sent);
        now = AckPulseQueue.T(1007); Assert.False(ShapingEffectsTests.Release(e, now));
        now = AckPulseQueue.T(1008); Assert.True(ShapingEffectsTests.Release(e, now)); Assert.Equal(pure, sent[0]);
        Assert.True(Capture(e, pure)); byte[] data = Packet(protocol: 6, payload: 1);
        Assert.False(Capture(e, data)); Assert.Equal(pure, sent[1]); Assert.Equal(data, sent[2]);
        foreach (byte flags in new byte[] { 0x18, 0x11, 0x12, 0x14, 0x50, 0x90 }) Assert.False(Capture(e, Packet(protocol: 6, payload: 0, flags: flags)));
        Assert.False(Capture(e, Packet()));
        for (int i = 0; i < 513; i++) Capture(e, pure);
        Assert.False(e.IsEffectActive); Assert.Contains("fail-open", e.LastShapingFallback); Assert.Equal(e.PureAcksHeld, e.PureAcksReleased);
        Assert.Equal(0, e.PacketsDropped);
    }
    [Fact]
    public void AckSchedulerHasSeedlessDeterministicSpacingFairnessTtlAndBounds()
    {
        var flow = new PulseShaper.Flow(6,"127.0.0.1",40000,"127.0.0.1",443);
        string Run()
        {
            var q = new AckPulseQueue(AckPulseQueue.T(1800)); var result = new List<long>();
            for (int i = 0; i < 12; i++) Assert.True(q.Enqueue(flow, new ThrottledPacket { Data = [1], Length = 40 }, AckPulseQueue.T(1000)));
            for (int ms = 1000; ms <= 1064; ms++) while (q.TryDequeue(AckPulseQueue.T(ms), out _)) result.Add(ms);
            Assert.Equal(new long[] {1008,1020,1032,1044,1056}, result.Take(5)); Assert.Equal(12, result.Count); Assert.Equal(0, q.Bytes);
            return string.Join(',', result);
        }
        Assert.Equal(Run(), Run());
        var bounded = new AckPulseQueue(AckPulseQueue.T(1800));
        for (ushort port = 0; port < 128; port++) Assert.True(bounded.Enqueue(flow with {DestinationPort=port}, new ThrottledPacket { Data=[1], Length=40 }, AckPulseQueue.T(1000)));
        Assert.False(bounded.Enqueue(flow with {DestinationPort=129}, new ThrottledPacket {Data=[1],Length=40},AckPulseQueue.T(1000)));
        Assert.Equal(128, bounded.Drain().Count()); Assert.Equal(0, bounded.Count);
    }
    [Fact]
    public void DscpFreezesForCurrentPulse_AndSettingsRoundtripWithoutAutoSave()
        => ConfigurationBehaviorTests.WithVm((vm,e,_,dir) =>
        {
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.DscpTrafficMarking); vm.DscpCodepoint = 46;
            vm.SelectedPulseStyle = "Legit"; vm.SaveSettingsCommand.Execute(null);
            string path = Path.Combine(dir,"settings.json"); byte[] before = File.ReadAllBytes(path);
            vm.DscpCodepoint = 8; vm.LoadSettings(); Assert.Equal(46, vm.DscpCodepoint); Assert.Equal(before, File.ReadAllBytes(path));
            ArmedCaptureTests.ModelArm(e); var sent = new List<byte[]>(); e.SendOverride=(p,n,_)=>{var b=new byte[n];Marshal.Copy(p,b,0,(int)n);sent.Add(b);return true;};
            Assert.True(e.TriggerArmedEffect(DiagnosticMode.DscpTrafficMarking,800)); vm.DscpCodepoint = 1; Capture(e,Packet());
            Assert.Equal(46, sent[0][1] >> 2); e.Stop();
            Assert.Equal(63,(new DarkDesync.Services.AppSettings{DscpCodepoint=999}).Validate().DscpCodepoint);
        }, outboundOnly:true);
}
