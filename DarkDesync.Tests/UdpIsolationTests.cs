using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public sealed class UdpIsolationTests
{
    internal static WinDivertNative.Address Event(ulong id = 1, ushort remotePort = 443,
        uint pid = 123, bool deleted = false)
    {
        return new WinDivertNative.Address
        {
            Flags = (uint)WinDivertNative.Layer.Flow |
                ((uint)(deleted ? WinDivertNative.Event.FlowDeleted : WinDivertNative.Event.FlowEstablished) << 8),
            Flow = new WinDivertNative.DataFlowRaw
            {
                EndpointId = id, ProcessId = pid, Protocol = 17, LocalPort = 40000, RemotePort = remotePort,
                LocalAddr0 = 0x7f000001, LocalAddr1 = 0xffff,
                RemoteAddr0 = 0x7f000002, RemoteAddr1 = 0xffff
            }
        };
    }

    [Fact]
    public void Ledger_RequiresPidLayerCompleteMappedIpv4Tuple()
    {
        var ledger = new UdpFlowLedger(123);
        ledger.Apply(Event(pid: 124));
        var e = Event(); e.Flags = (uint)WinDivertNative.Layer.Socket | ((uint)WinDivertNative.Event.FlowEstablished << 8); ledger.Apply(e);
        e = Event(); e.Flags |= 1u << 20; ledger.Apply(e); // IPv6 never accepted.
        e = Event(); e.Flow.RemotePort = 0; ledger.Apply(e);
        e = Event(); e.Flow.LocalPort = 0; ledger.Apply(e);
        e = Event(); e.Flow.Protocol = 6; ledger.Apply(e);
        e = Event(); e.Flow.RemoteAddr1 = 0; ledger.Apply(e); // Not an IPv4-mapped address.
        e = Event(); e.Flow.RemoteAddr0 = 0xe0000001; ledger.Apply(e); // Multicast.
        Assert.Empty(ledger.Snapshot());
        Assert.True(ledger.Apply(Event()));
        Assert.Equal(DiagnosticModesTests.Endpoint() with { Protocol = 17 }, Assert.Single(ledger.Snapshot()));
    }

    [Fact]
    public void Ledger_SameSocketDifferentRemotes_DeletionUsesFullTupleAndEndpointGeneration()
    {
        var ledger = new UdpFlowLedger(123);
        ledger.Apply(Event(remotePort: 443)); ledger.Apply(Event(remotePort: 444));
        Assert.Equal(2, ledger.Snapshot().Length); // UDP endpoint ID can be shared by flows.
        ledger.Apply(Event(remotePort: 443, deleted: true));
        Assert.Equal((ushort)444, Assert.Single(ledger.Snapshot()).RemotePort);
        ledger.Apply(Event(id: 2, remotePort: 443));
        ledger.Apply(Event(id: 1, remotePort: 443, deleted: true)); // Old socket must not delete new socket evidence.
        Assert.Equal(2, ledger.Snapshot().Length);
        ledger.Apply(Event(id: 2, remotePort: 443, pid: 124, deleted: true));
        Assert.Equal(2, ledger.Snapshot().Length);
    }

    [Fact]
    public void Ledger_OverflowInvalidatesRatherThanSilentlyDroppingEvidence()
    {
        var ledger = new UdpFlowLedger(123);
        for (ushort i = 1; i <= 256; i++) Assert.True(ledger.Apply(Event(remotePort: i)));
        Assert.False(ledger.Apply(Event(remotePort: 257)));
        Assert.Empty(ledger.Snapshot());
    }

    [Fact]
    public void Ownership_RejectsSharedPortOtherPidOtherIpAndIpv6DualStack()
    {
        var flow = DiagnosticModesTests.Endpoint() with { Protocol = 17 };
        var owner = new ProcessNetworkScanner.UdpBinding(123, flow.LocalPort, flow.LocalAddr);
        Assert.True(ProcessNetworkScanner.HasExclusiveUdpBinding(123, flow, [owner]));
        Assert.True(ProcessNetworkScanner.HasExclusiveUdpBinding(123, flow, [owner with { LocalAddr = 0 }]));
        Assert.False(ProcessNetworkScanner.HasExclusiveUdpBinding(123, flow, []));
        Assert.False(ProcessNetworkScanner.HasExclusiveUdpBinding(123, flow, [owner with { ProcessId = 124 }]));
        Assert.False(ProcessNetworkScanner.HasExclusiveUdpBinding(123, flow, [owner, owner with { ProcessId = 124 }]));
        Assert.False(ProcessNetworkScanner.HasExclusiveUdpBinding(123, flow, [owner, owner with { LocalAddr = 0x0300007f }]));
        Assert.False(ProcessNetworkScanner.HasExclusiveUdpBinding(123, flow, [owner, owner with { IPv6 = true }]));
        Assert.False(ProcessNetworkScanner.HasExclusiveUdpBinding(123, flow, [owner with { IPv6 = true }]));
        Assert.False(ProcessNetworkScanner.HasExclusiveUdpBinding(123, flow with { RemotePort = 0 }, [owner]));
        Assert.False(ProcessNetworkScanner.HasExclusiveUdpBinding(123, flow, [owner with { LocalAddr = 0x0300007f }]));
    }

    [Theory]
    [InlineData(DiagnosticMode.OutboundFreeze, true, 0, 1, 0)]
    [InlineData(DiagnosticMode.OutboundFreeze, false, 0, 0, 1)]
    [InlineData(DiagnosticMode.BurstLoss, true, 1, 0, 0)]
    [InlineData(DiagnosticMode.BurstLoss, false, 1, 0, 0)]
    [InlineData(DiagnosticMode.AsymmetricLoss, true, 1, 0, 0)]
    [InlineData(DiagnosticMode.AsymmetricLoss, false, 0, 0, 1)]
    public void UdpModes_UseSameEngineAndDirectionRules(DiagnosticMode mode, bool outbound, long dropped, long held, long passed)
    {
        using var engine = DiagnosticModesTests.Engine(); DiagnosticModesTests.Authorize(engine);
        var ledger = new UdpFlowLedger(123); ledger.Apply(Event());
        engine.UdpFlowsOverride = ledger.Snapshot; engine.UdpBindingOverride = _ => true;
        engine.ActiveDiagnosticMode = mode;
        engine.BurstLossPercentage = engine.AsymmetricOutboundLossPercentage = 100;
        engine.AsymmetricInboundLossPercentage = 0;
        engine.StateMachine.TryActivate();
        DiagnosticModesTests.Capture(engine, outbound, protocol: 17);
        Assert.Equal(dropped, engine.PacketsDropped); Assert.Equal(held, engine.PacketsThrottled);
        Assert.Equal(passed, engine.PacketsPassedThrough);
        engine.StateMachine.TryBeginRestore(); engine.FlushThrottleQueue();
        Assert.Equal(passed + held, engine.PacketsPassedThrough);
    }

    [Fact]
    public void UdpPortsAloneUnknownRemoteDeletedFlowAndAmbiguousBinding_NeverAuthorize()
    {
        using var engine = DiagnosticModesTests.Engine(); DiagnosticModesTests.Authorize(engine);
        engine.EndpointsOverride = _ => [DiagnosticModesTests.Endpoint() with { Protocol = 17, RemotePort = 0, RemoteAddr = 0 }];
        engine.ActiveDiagnosticMode = DiagnosticMode.BurstLoss; engine.BurstLossPercentage = 100;
        engine.StateMachine.TryActivate();
        var ledger = new UdpFlowLedger(123);
        engine.UdpFlowsOverride = ledger.Snapshot; engine.UdpBindingOverride = _ => true;
        DiagnosticModesTests.Capture(engine, protocol: 17); // Binding alone is not positive evidence.
        ledger.Apply(Event());
        DiagnosticModesTests.Capture(engine, remotePort: 444, protocol: 17); // Same local port, other peer.
        DiagnosticModesTests.Capture(engine, protocol: 17, fragment: true);
        ledger.Apply(Event(deleted: true)); DiagnosticModesTests.Capture(engine, protocol: 17);
        ledger.Apply(Event()); engine.UdpBindingOverride = _ => false; DiagnosticModesTests.Capture(engine, protocol: 17);
        engine.UdpBindingOverride = _ => throw new InvalidOperationException("owner table unavailable");
        DiagnosticModesTests.Capture(engine, protocol: 17);
        Assert.Equal(6, engine.PacketsPassedThrough); Assert.Equal(0, engine.PacketsDropped);
    }

    [Fact]
    public void MinimalUdpDatagram_IsParsed_AndMalformedLengthOrIpv6FailClosed()
    {
        using var engine = DiagnosticModesTests.Engine(); DiagnosticModesTests.Authorize(engine);
        engine.UdpFlowsOverride = () => [DiagnosticModesTests.Endpoint() with { Protocol = 17 }];
        engine.UdpBindingOverride = _ => true;
        var bytes = DiagnosticModesTests.Packet(true, protocol: 17)[..28];
        bytes[3] = 28; bytes[25] = 8;
        var ptr = Marshal.AllocHGlobal(64);
        try
        {
            var addr = new WinDivertNative.Address { Flags = 1u << 17 };
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Assert.True(engine.IsAuthorizedTraffic(ptr, 28, addr));
            bytes[25] = 7; Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Assert.False(engine.IsAuthorizedTraffic(ptr, 28, addr));
            bytes[0] = 0x60; Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Assert.False(engine.IsAuthorizedTraffic(ptr, 28, addr));
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [Fact]
    public void Filters_ArePidOnlyMetadataAndFullTuplePayload_NotGlobalOrPortOnly()
    {
        Assert.Equal("processId == 123 and protocol == 17 and not ipv6", FlowTracker.ObservationFilter(123));
        var flow = DiagnosticModesTests.Endpoint() with { Protocol = 17 };
        string filter = FilterBuilder.BuildScopedFilter([flow], "true");
        Assert.Contains("udp and", filter); Assert.Contains("ip.SrcAddr == 127.0.0.1", filter);
        Assert.Contains("udp.SrcPort == 40000", filter); Assert.Contains("ip.DstAddr == 127.0.0.2", filter);
        Assert.Contains("udp.DstPort == 443", filter);
        Assert.DoesNotContain("tcp", filter);
        Assert.Equal("false", FilterBuilder.BuildScopedFilter([flow with { RemoteAddr = 0, RemotePort = 0 }], "true"));
    }
}
