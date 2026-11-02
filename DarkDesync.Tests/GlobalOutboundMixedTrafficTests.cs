using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

// Independent audit: GLOBAL outbound intercepts TCP, IPv6 and ICMP as well as UDP.
// Unrelated protocols must pass through without cancelling an active UDP
// size-aware pulse, otherwise the mode is unusable on a normal desktop.
public sealed class GlobalOutboundMixedTrafficTests
{
    [Fact]
    public void TcpIpv6IcmpPassThroughWithoutCancellingActiveGlobalUdpSizePulse()
    {
        using var engine = DiagnosticModesTests.Engine();
        Assert.True(engine.ConfigureScopedProfile(DiagnosticMode.PacketSizeAwareShaping));
        engine.GlobalPacketSizeConsent = true;
        ArmedCaptureTests.ModelArm(engine);
        ArmedCaptureTests.Set(engine, "_timestamp", (Func<long>)(() => ScopedPulseScheduler.T(1000)));
        Assert.True(engine.TriggerArmedEffect(DiagnosticMode.PacketSizeAwareShaping, 200));
        Assert.True(engine.IsEffectActive);
        Assert.False(ScopedPulseTests.Capture(engine, 1, outbound: true, protocol: 6));
        Assert.True(engine.IsEffectActive); // unrelated TCP is not a UDP safety failure
        // IPv6 is also captured by literal GLOBAL outbound but is outside
        // the IPv4 UDP shaper; it must not revoke the UDP pulse either.
        var ipv6 = new byte[40]; ipv6[0] = 0x60; ipv6[6] = 17;
        IntPtr ptr = Marshal.AllocHGlobal(ipv6.Length);
        try
        {
            Marshal.Copy(ipv6, 0, ptr, ipv6.Length);
            Assert.False(engine.ProcessCapturedPacket(ptr, (uint)ipv6.Length,
                new WinDivertNative.Address { Flags = 1u << 17 }, new Random(1)));
        }
        finally { Marshal.FreeHGlobal(ptr); }
        Assert.True(engine.IsEffectActive);
        Assert.False(ScopedPulseTests.Capture(engine, 3, outbound: true, protocol: 1));
        Assert.True(engine.IsEffectActive); // ICMP is also disjoint from IPv4 UDP.
        Assert.True(ScopedPulseTests.Capture(engine, 2, outbound: true, protocol: 17));
        engine.CancelArmedEffect();
        Assert.Equal(0, engine.PacketsDropped);
    }

    [Theory]
    [InlineData(false, (byte)17)] // malformed IPv4 UDP must still drain/cancel
    [InlineData(true, (byte)17)] // impostor UDP cannot join the scheduler
    [InlineData(true, (byte)6)] // disjoint protocol cannot bypass impostor safety
    public void InvalidUdpOrImpostor_DrainsBeforeCurrentPacket_CancelsWithoutReplay(bool impostor, byte protocol)
    {
        using var engine = DiagnosticModesTests.Engine();
        Assert.True(engine.ConfigureScopedProfile(DiagnosticMode.PacketSizeAwareShaping));
        engine.GlobalPacketSizeConsent = true;
        ArmedCaptureTests.ModelArm(engine);
        ArmedCaptureTests.Set(engine, "_timestamp", (Func<long>)(() => ScopedPulseScheduler.T(1000)));
        var sent = new List<byte>();
        engine.SendOverride = (p, n, _) => { sent.Add(Marshal.ReadByte(p, (int)n - 1)); return true; };
        Assert.True(engine.TriggerArmedEffect(DiagnosticMode.PacketSizeAwareShaping, 200));
        Assert.True(ScopedPulseTests.Capture(engine, 1, outbound: true));
        Assert.True(ScopedPulseTests.Capture(engine, 2, outbound: true));
        Assert.Empty(sent);
        byte[] bytes = DiagnosticModesTests.Packet(true, 443, protocol);
        if (!impostor) bytes[24] = bytes[25] = 0; // invalid UDP length, IP/protocol remain IPv4 UDP
        bytes[^1] = 9;
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Assert.False(engine.ProcessCapturedPacket(ptr, (uint)bytes.Length,
                new WinDivertNative.Address { Flags = (1u << 17) | (impostor ? 1u << 19 : 0) }, new Random(1)));
        }
        finally { Marshal.FreeHGlobal(ptr); }
        Assert.False(engine.IsEffectActive);
        Assert.Equal(new byte[] { 1, 2, 9 }, sent);
        Assert.Equal(0, engine.PacketsDropped);
        engine.Stop();
        Assert.Equal(new byte[] { 1, 2, 9 }, sent);
    }
}
