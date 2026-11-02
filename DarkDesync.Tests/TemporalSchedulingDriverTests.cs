using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

[Collection("WinDivert controlled driver")]
public sealed class TemporalSchedulingDriverTests(ITestOutputHelper output)
{
    [GlobalDriverTheory,Trait("Category","Driver")]
    [InlineData(DiagnosticMode.InterPacketGapEqualization,false)] [InlineData(DiagnosticMode.InterPacketGapEqualization,true)]
    [InlineData(DiagnosticMode.DeadlineAwareScheduling,false)] [InlineData(DiagnosticMode.DeadlineAwareScheduling,true)]
    public async Task OwnedLoopback_A_B_RealGapHistogramDeadlineFifoAndStop(DiagnosticMode mode,bool v6)
    {
        IPAddress ip=v6?IPAddress.IPv6Loopback:IPAddress.Loopback;
        using var receiver=new UdpClient(new IPEndPoint(ip,0));using var sender=new UdpClient(new IPEndPoint(ip,0));sender.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
        int port=((IPEndPoint)sender.Client.LocalEndPoint!).Port;
        using var before=new PacketWireSpy($"outbound and udp and udp.SrcPort == {port}",100);
        using var after=new PacketWireSpy($"outbound and udp and udp.SrcPort == {port}",-1000);
        using var e=DiagnosticModesTests.Engine();e.UpdateFilter(FilterMode.LegacyGlobalOutbound);Assert.True(e.BeginLegacyGlobalContinuous(DiagnosticMode.None));
        byte[] Payload(byte id){var b=Enumerable.Range(0,512).Select(i=>(byte)(i^id)).ToArray();b[0]=id;return b;}
        async Task Batch(byte start)
        {
            var read=Task.Run(async()=>
            {
                for(byte i=start;i<start+6;i++)
                {var p=await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(Payload(i),p.Buffer);}
            });
            for(byte i=start;i<start+6;i++)await sender.SendAsync(Payload(i),512);
            await read.WaitAsync(TimeSpan.FromSeconds(4));
            await UdpDriverIntegrationTests.WaitUntil(()=>after.Frames.Count(f=>f.Bytes[v6?48:28]>=start&&f.Bytes[v6?48:28]<start+6)==6);
        }
        await Batch(10);Assert.Equal(0,e.TemporalPacketsHeld);
        Assert.True(e.TriggerArmedEffect(mode,800));await Batch(30);
        Assert.True(e.TemporalPacketsHeld>=6);Assert.True(e.TemporalPacketsReleased>=6);e.Stop();
        var input=before.Frames.ToArray();var frames=after.Frames.ToArray();int offset=v6?48:28;
        double[] Gaps(byte start)
        {
            var t=frames.Where(f=>f.Bytes[offset]>=start&&f.Bytes[offset]<start+6).Select(f=>f.Timestamp*1000d/Stopwatch.Frequency).ToArray();
            return t.Zip(t.Skip(1),(a,b)=>b-a).ToArray();
        }
        var a=Gaps(10);var b=Gaps(30);
        var latency=Enumerable.Range(30,6).Select(id=>
        {
            var original=input.Single(f=>f.Bytes[offset]==id);var released=frames.Single(f=>f.Bytes[offset]==id);
            // Unmodified wire packet, allowing Windows loopback IPv4 header checksum normalization only.
            var x=original.Bytes.ToArray();var y=released.Bytes.ToArray();if(!v6)x[10]=x[11]=y[10]=y[11]=0;Assert.Equal(x,y);
            return (released.Timestamp-original.Timestamp)*1000d/Stopwatch.Frequency;
        }).ToArray();
        if(mode==DiagnosticMode.InterPacketGapEqualization)
        {Assert.True(b.Count(ms=>ms>=4)>=3,JsonSerializer.Serialize(b));Assert.True(b.Sum()>a.Sum()+10);}
        else {Assert.All(latency,ms=>Assert.InRange(ms,5,300));}
        byte[] sentinel=Payload(90);await sender.SendAsync(sentinel,512);Assert.Equal(sentinel,(await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        await Task.Delay(40);Assert.Equal(0,receiver.Available);Assert.Equal(e.TemporalPacketsHeld,e.TemporalPacketsReleased);Assert.Equal(0,e.PacketsFailedToSend);
        Assert.True(e.BeginLegacyGlobalContinuous(DiagnosticMode.None));Assert.True(e.TriggerArmedEffect(mode,800));
        var pending=Payload(91);await sender.SendAsync(pending,512);e.Stop();Assert.Equal(pending,(await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.False(e.IsRunning);Assert.False(e.IsEffectActive);Assert.Equal(e.TemporalPacketsHeld,e.TemporalPacketsReleased);
        output.WriteLine($"{mode} IPv{(v6?6:4)} own UDP native A/B: baseline gaps ms={JsonSerializer.Serialize(a)}; effect gaps ms={JsonSerializer.Serialize(b)}; pre/post kernel QPC latencies ms={JsonSerializer.Serialize(latency)}; FIFO/exact payloads; STOP/restart sentinel drained, no late copy. Timing is local Windows measurement, not real-time guarantee.");
    }

    [GlobalDriverTheory,Trait("Category","Driver")]
    [InlineData(DiagnosticMode.InterPacketGapEqualization,false)] [InlineData(DiagnosticMode.InterPacketGapEqualization,true)]
    [InlineData(DiagnosticMode.DeadlineAwareScheduling,false)] [InlineData(DiagnosticMode.DeadlineAwareScheduling,true)]
    public async Task RealTcpPayloadSurvivesPulseAndStop(DiagnosticMode mode,bool v6)
    {
        IPAddress ip=v6?IPAddress.IPv6Loopback:IPAddress.Loopback;var listener=new TcpListener(ip,0);listener.Start();
        try
        {
            using var client=new TcpClient(v6?AddressFamily.InterNetworkV6:AddressFamily.InterNetwork);client.NoDelay=true;
            await client.ConnectAsync(ip,((IPEndPoint)listener.LocalEndpoint).Port);using var server=await listener.AcceptTcpClientAsync();server.NoDelay=true;
            using var e=DiagnosticModesTests.Engine();e.UpdateFilter(FilterMode.LegacyGlobalOutbound);Assert.True(e.BeginLegacyGlobalContinuous(DiagnosticMode.None));
            byte[] bytes=Enumerable.Range(0,2048).Select(i=>(byte)(i*29)).ToArray();
            Assert.True(e.TriggerArmedEffect(mode,800));var clock=Stopwatch.StartNew();
            await client.GetStream().WriteAsync(bytes);var received=new byte[bytes.Length];
            await server.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(bytes,received);
            Assert.True(e.TemporalPacketsHeld>0);e.Stop();Assert.Equal(e.TemporalPacketsHeld,e.TemporalPacketsReleased);
            await server.GetStream().WriteAsync(bytes);await client.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(bytes,received);
            output.WriteLine($"{mode} real TCP IPv{(v6?6:4)}: exact 2048-byte stream delivered during pulse, elapsed {clock.Elapsed.TotalMilliseconds:F2} ms, held/released {e.TemporalPacketsHeld}/{e.TemporalPacketsReleased}; reverse stream exact after STOP. No retransmission advantage claim.");
        }
        finally {listener.Stop();}
    }

    [GlobalDriverTheory,InlineData(true),Trait("Category","Driver")]
    public void ReadOnlyTopologyInventoryDoesNotPretendSteeringOrNatProof(bool readOnly)
    {
        Assert.True(readOnly);
        var adapters=NetworkInterface.GetAllNetworkInterfaces().Select(n=>new{n.NetworkInterfaceType,n.OperationalStatus,
            Gateways=n.GetIPProperties().GatewayAddresses.Select(g=>g.Address.AddressFamily.ToString()).ToArray()}).ToArray();
        output.WriteLine("Read-only isolated Windows adapter types/status/gateway address families (no IP/credentials): "+JsonSerializer.Serialize(adapters));
        output.WriteLine("No controlled second Windows route/gateway plus receiving server, no controlled NAT mapping validator and no verified external UDMUX channel schema. Multipath/NAT unavailable; UDMUX inconclusive. Interface count alone is NOT proof of distinct paths. No route/NAT/firewall/registry/MTU write performed.");
        Assert.DoesNotContain(ModeCatalog.OutboundChoices.Keys,n=>n.Contains("UDMUX")||n.Contains("Multipath")||n.Contains("NAT"));
    }
}
