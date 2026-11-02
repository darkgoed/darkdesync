using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

[Collection("WinDivert controlled driver")]
public sealed class NetworkTechniquesDriverTests(ITestOutputHelper output)
{
    [GlobalDriverTheory, Trait("Category", "Driver")]
    [InlineData(DiagnosticMode.PacketDuplication,false)] [InlineData(DiagnosticMode.PacketDuplication,true)]
    [InlineData(DiagnosticMode.EcnCongestionMarking,false)] [InlineData(DiagnosticMode.EcnCongestionMarking,true)]
    [InlineData(DiagnosticMode.DscpTrafficMarking,false)] [InlineData(DiagnosticMode.DscpTrafficMarking,true)]
    public async Task OwnUdpWire_ABHeadersCopiesPayloadChecksumsAndStop(DiagnosticMode mode,bool v6)
    {
        IPAddress ip=v6?IPAddress.IPv6Loopback:IPAddress.Loopback;
        using var receiver=new UdpClient(new IPEndPoint(ip,0)); using var source=new UdpClient(new IPEndPoint(ip,0));
        ushort src=(ushort)((IPEndPoint)source.Client.LocalEndPoint!).Port, dst=(ushort)((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        using var spy=new PacketWireSpy($"outbound and udp and udp.SrcPort == {src} and udp.DstPort == {dst}",-1000);
        IntPtr injector=WinDivertNative.WinDivertOpen("false",WinDivertNative.Layer.Network,1000,WinDivertNative.FlagSendOnly);
        Assert.NotEqual(WinDivertNative.InvalidHandle,injector);
        using var e=DiagnosticModesTests.Engine(); e.UpdateFilter(FilterMode.LegacyGlobalOutbound); e.DscpCodepoint=46;
        Assert.True(e.BeginLegacyGlobalContinuous(DiagnosticMode.None)); Assert.Equal("outbound",e.CurrentFilter);
        try
        {
            byte[] Make(byte marker,byte ecn)
            { var b=NetworkTechniquesTests.Packet(v6,17,ecn,24,sourcePort:src,destinationPort:dst); b[^1]=marker;NetworkTechniquesTests.RepairReference(b,v6,17);return b; }
            async Task<byte[]> Send(byte marker,byte ecn)
            {
                byte[] original=Make(marker,ecn); PacketWireSpy.InjectOwnLoopback(injector,original);
                var datagram=await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(original.Skip(v6?48:28),datagram.Buffer);
                await UdpDriverIntegrationTests.WaitUntil(()=>spy.Frames.Any(f=>f.Bytes[^1]==marker)); return original;
            }
            byte[] baseline=await Send(70,2);
            Assert.Equal(baseline,spy.Frames.Single(f=>f.Bytes[^1]==70).Bytes); Assert.Equal(0,e.DuplicateCopiesSent+e.EcnPacketsMarked+e.DscpPacketsMarked);
            Assert.True(e.TriggerArmedEffect(mode,800)); byte[] during=await Send(71,2);
            if(mode==DiagnosticMode.PacketDuplication)
            {
                Assert.Equal(during.Skip(v6?48:28),(await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
                await UdpDriverIntegrationTests.WaitUntil(()=>spy.Frames.Count(f=>f.Bytes[^1]==71)==2);
                Assert.All(spy.Frames.Where(f=>f.Bytes[^1]==71),f=>Assert.Equal(during,f.Bytes)); Assert.True(e.DuplicateCopiesSent>=1);
            }
            else
            {
                var observed=spy.Frames.Single(f=>f.Bytes[^1]==71).Bytes;
                Assert.True(TechniquePackets.TryParse(observed,out var l)); NetworkTechniquesTests.AssertChecksums(observed);
                Assert.Equal(mode==DiagnosticMode.EcnCongestionMarking?(byte)3:(byte)((46<<2)|2),TechniquePackets.TrafficClass(observed,l));
                Assert.Equal(during.Skip(l.Transport+l.HeaderLength),observed.Skip(l.Transport+l.HeaderLength));
                if(mode==DiagnosticMode.EcnCongestionMarking)
                {
                    Assert.True(e.EcnPacketsMarked>=1); byte[] notEct=await Send(73,0);
                    Assert.Equal(notEct,spy.Frames.Single(f=>f.Bytes[^1]==73).Bytes);
                }
                else Assert.True(e.DscpPacketsMarked>=1);
            }
            e.Stop(); byte[] after=await Send(72,2); Assert.Equal(after,spy.Frames.Single(f=>f.Bytes[^1]==72).Bytes);
            await Task.Delay(40); Assert.Equal(0,receiver.Available); Assert.Equal(0,e.PacketsFailedToSend);
            output.WriteLine($"{mode} IPv{(v6?6:4)}: actual WinDivert chain injector1000 -> production0 -> sniff-1000; own bound loopback endpoints only; A/B/STOP parsed headers, payload and checksum verified; copies={e.DuplicateCopiesSent}, ECN={e.EcnPacketsMarked}, DSCP={e.DscpPacketsMarked}. Not an endpoint ECN-response or routing-priority claim.");
        }
        finally {e.Stop();WinDivertNative.WinDivertClose(injector);}
    }

    [GlobalDriverTheory,Trait("Category","Driver")]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualTcpPureAcksArePacedWithoutFabrication_AndDscpWorksOnTcp(bool v6)
    {
        IPAddress ip=v6?IPAddress.IPv6Loopback:IPAddress.Loopback;
        var listener=new TcpListener(ip,0);listener.Start();
        try
        {
            using var client=new TcpClient(v6?AddressFamily.InterNetworkV6:AddressFamily.InterNetwork);client.NoDelay=true;
            await client.ConnectAsync(ip,((IPEndPoint)listener.LocalEndpoint).Port);
            using var server=await listener.AcceptTcpClientAsync();server.NoDelay=true;
            int port=((IPEndPoint)client.Client.LocalEndPoint!).Port;
            using var before=new PacketWireSpy($"outbound and tcp and tcp.SrcPort == {port}",100);
            using var after=new PacketWireSpy($"outbound and tcp and tcp.SrcPort == {port}",-1000);
            using var e=DiagnosticModesTests.Engine();e.UpdateFilter(FilterMode.LegacyGlobalOutbound);e.DscpCodepoint=46;
            Assert.True(e.BeginLegacyGlobalContinuous(DiagnosticMode.None));
            Assert.True(e.TriggerArmedEffect(DiagnosticMode.TcpAckPacing,800));
            byte[] bytes=new byte[4096];for(int i=0;i<bytes.Length;i++)bytes[i]=(byte)(i*7);
            for(int i=0;i<4;i++)
            {
                await server.GetStream().WriteAsync(bytes);
                byte[] got=new byte[bytes.Length];await client.GetStream().ReadExactlyAsync(got).AsTask().WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(bytes,got);
            }
            await UdpDriverIntegrationTests.WaitUntil(()=>e.PureAcksHeld>0&&e.PureAcksReleased>0);
            e.CancelArmedEffect(); Assert.Equal(e.PureAcksHeld,e.PureAcksReleased);
            static string Key(byte[] p)
            {var b=p.ToArray();int offset=b[0]>>4==6?40:(b[0]&15)*4;if(offset==20)b[10]=b[11]=0;b[offset+16]=b[offset+17]=0;return Convert.ToHexString(b);}
            var inputs=before.Frames.Where(f=>TechniquePackets.TryParse(f.Bytes,out var l)&&l.PureAck).ToArray();
            var outputs=after.Frames.Where(f=>TechniquePackets.TryParse(f.Bytes,out var l)&&l.PureAck).ToArray();
            var delays=(from a in inputs from b in outputs where Key(a.Bytes)==Key(b.Bytes)&&b.Timestamp>=a.Timestamp select (b.Timestamp-a.Timestamp)*1000d/Stopwatch.Frequency).ToArray();
            Assert.NotEmpty(delays);Assert.Contains(delays,ms=>ms>=4&&ms<250);
            Assert.All(outputs,f=>Assert.Contains(inputs,a=>Key(a.Bytes)==Key(f.Bytes))); // Never fabricated ACK.
            Assert.True(e.TriggerArmedEffect(DiagnosticMode.DscpTrafficMarking,800));
            int observedBefore=after.Frames.Count;
            await client.GetStream().WriteAsync(new byte[37]);var read=new byte[37];await server.GetStream().ReadExactlyAsync(read);
            await UdpDriverIntegrationTests.WaitUntil(()=>after.Frames.Skip(observedBefore).Any(f=>TechniquePackets.TryParse(f.Bytes,out var l)&&(TechniquePackets.TrafficClass(f.Bytes,l)>>2)==46));
            var marked=after.Frames.Skip(observedBefore).First(f=>TechniquePackets.TryParse(f.Bytes,out var l)&&(TechniquePackets.TrafficClass(f.Bytes,l)>>2)==46);
            NetworkTechniquesTests.AssertChecksums(marked.Bytes);e.Stop();
            output.WriteLine($"TCP IPv{(v6?6:4)} real connected endpoints: pure ACK held/released={e.PureAcksHeld}/{e.PureAcksReleased}; QPC pre/post delays ms=[{string.Join(',',delays.Select(x=>x.ToString("F2")))}]; exact ACK identities preserved; real TCP DSCP46 checksum verified; no UDP ACK invention.");
        }
        finally {listener.Stop();}
    }

    [GlobalDriverTheory,InlineData(true),Trait("Category","Driver")]
    public async Task MtuLabGeneratesOnlyOwnedLoopbackDfProbesAndNeverClaimsFragmentation(bool ownLab)
    {
        Assert.True(ownLab);
        using var spy=new PacketWireSpy("outbound and loopback and udp",-1000);
        using var e=DiagnosticModesTests.Engine();e.UpdateFilter(FilterMode.LegacyGlobalOutbound);Assert.True(e.BeginLegacyGlobalContinuous(DiagnosticMode.None));
        Assert.Null(e.LastMtuReport);Assert.True(e.TriggerArmedEffect(DiagnosticMode.MtuFragmentationLab,800));
        await e.MtuLabWork!.WaitAsync(TimeSpan.FromSeconds(3));var report=e.LastMtuReport!;
        Assert.NotNull(report);Assert.True(report.Completed,JsonSerializer.Serialize(report));Assert.Equal(8,report.Probes.Count);
        Assert.All(report.Probes,p=>Assert.True(p.ReceivedExact,JsonSerializer.Serialize(report)));
        Assert.True(report.InterfaceMtu>0);Assert.True(report.LargestDeliveredIpBytes>=4028);Assert.False(report.FragmentationProven);
        await UdpDriverIntegrationTests.WaitUntil(()=>spy.Frames.Count(f=>TechniquePackets.TryParse(f.Bytes,out var l)&&l.Protocol==17&&TechniquePackets.Flow(f.Bytes,l).SourcePort==report.SourcePort&&TechniquePackets.Flow(f.Bytes,l).DestinationPort==report.DestinationPort)>=8);
        var frames=spy.Frames.Where(f=>TechniquePackets.TryParse(f.Bytes,out var l)&&TechniquePackets.Flow(f.Bytes,l).SourcePort==report.SourcePort&&TechniquePackets.Flow(f.Bytes,l).DestinationPort==report.DestinationPort).ToArray();
        Assert.Equal(8,frames.Length);foreach(var f in frames)Assert.Equal(4,f.Bytes[0]>>4);
        e.Stop();Assert.False(e.MtuLabWork is{IsCompleted:false});Assert.False(e.IsCaptureRequested);
        output.WriteLine("MTU report (own loopback only, no host config writes): "+JsonSerializer.Serialize(report));
        output.WriteLine("Actual own wire IP sizes/DF: "+JsonSerializer.Serialize(frames.Select(f=>new{Bytes=f.Bytes.Length,DF=(BinaryPrimitives.ReadUInt16BigEndian(f.Bytes.AsSpan(6))&0x4000)!=0})));
        Assert.True(e.BeginLegacyGlobalContinuous(DiagnosticMode.None));Assert.True(e.TriggerArmedEffect(DiagnosticMode.MtuFragmentationLab,800));e.Stop();
        Assert.False(e.MtuLabWork is{IsCompleted:false});
    }
}
