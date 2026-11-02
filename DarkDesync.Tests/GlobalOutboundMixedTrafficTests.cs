using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

// Independent audit: GLOBAL outbound intercepts TCP as well as UDP. An
// unrelated TCP packet must pass through without cancelling an active UDP
// size-aware pulse, otherwise the mode is unusable on a normal desktop.
public sealed class GlobalOutboundMixedTrafficTests
{
    [Fact]
    public void TcpPassesThroughWithoutCancellingActiveGlobalUdpSizePulse()
    {
        using var engine = DiagnosticModesTests.Engine();
        Assert.True(engine.ConfigureScopedProfile(DiagnosticMode.PacketSizeAwareShaping));
        engine.GlobalPacketSizeConsent = true;
        ArmedCaptureTests.ModelArm(engine);
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
        Assert.True(ScopedPulseTests.Capture(engine, 2, outbound: true, protocol: 17));
        engine.CancelArmedEffect();
        Assert.Equal(0, engine.PacketsDropped);
    }
}
