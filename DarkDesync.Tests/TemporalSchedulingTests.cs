using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public sealed class TemporalSchedulingTests
{
    private static readonly PulseShaper.Flow Flow = new(17,"127.0.0.1",41000,"127.0.0.1",42000);
    private static ThrottledPacket P(byte id,int size=100) => new(){Data=[id],Length=(uint)size};
    private static List<(byte Id,int Ms)> Schedule(DiagnosticMode mode,int size=100)
    {
        var q=new TemporalPulseQueue(mode,TemporalPulseQueue.T(1800));var result=new List<(byte,int)>();
        for(byte i=0;i<10;i++)Assert.True(q.Enqueue(Flow,P(i,size),TemporalPulseQueue.T(1000)));
        for(int ms=1000;ms<=1100;ms++)while(q.TryDequeue(TemporalPulseQueue.T(ms),out var p))result.Add((p!.Data[0],ms));
        Assert.Equal(0,q.Count);Assert.Equal(0,q.Bytes);Assert.Equal(Enumerable.Range(0,10).Select(i=>(byte)i),result.Select(x=>x.Item1));return result;
    }
    [Fact]
    public void GapIsDeterministicSizeIndependentRegularSpacing_NotFixedDelayOrBytesPerSecond()
    {
        var a=Schedule(DiagnosticMode.InterPacketGapEqualization);Assert.Equal(a,Schedule(DiagnosticMode.InterPacketGapEqualization));
        Assert.Equal(a,Schedule(DiagnosticMode.InterPacketGapEqualization,4000));
        Assert.All(a.Zip(a.Skip(1)),pair=>Assert.Equal(6,pair.Second.Ms-pair.First.Ms));
        Assert.NotEqual(a,Schedule(DiagnosticMode.DeadlineAwareScheduling));
    }
    [Fact]
    public void DeadlineUsesAgeBudgetAndBacklog_HeadFifoAndEdfFairness()
    {
        var q=new TemporalPulseQueue(DiagnosticMode.DeadlineAwareScheduling,TemporalPulseQueue.T(1800));
        Assert.True(q.Enqueue(Flow,P(1,1000),TemporalPulseQueue.T(1000)));
        Assert.True(q.Enqueue(Flow with{SourcePort=41001},P(2),TemporalPulseQueue.T(1000)));
        Assert.False(q.TryDequeue(TemporalPulseQueue.T(1013),out _));
        Assert.True(q.TryDequeue(TemporalPulseQueue.T(1014),out var small));Assert.Equal(2,small!.Data[0]);
        Assert.False(q.TryDequeue(TemporalPulseQueue.T(1047),out _));
        Assert.True(q.TryDequeue(TemporalPulseQueue.T(1048),out var large));Assert.Equal(1,large!.Data[0]);
        var result=Schedule(DiagnosticMode.DeadlineAwareScheduling,1000);
        Assert.True(result[0].Ms<1048);Assert.All(result,p=>Assert.InRange(p.Ms,1002,1048));
        var fifo=new TemporalPulseQueue(DiagnosticMode.DeadlineAwareScheduling,TemporalPulseQueue.T(1800));
        fifo.Enqueue(Flow,P(1,1000),TemporalPulseQueue.T(1000));fifo.Enqueue(Flow,P(2),TemporalPulseQueue.T(1001));
        Assert.False(fifo.TryDequeue(TemporalPulseQueue.T(1045),out _));
        Assert.True(fifo.TryDequeue(TemporalPulseQueue.T(1046),out var head));Assert.Equal(1,head!.Data[0]);
    }
    [Theory]
    [InlineData(DiagnosticMode.InterPacketGapEqualization)] [InlineData(DiagnosticMode.DeadlineAwareScheduling)]
    public void FairnessDeadlinePulseClampAndAllBounds(DiagnosticMode mode)
    {
        var q=new TemporalPulseQueue(mode,TemporalPulseQueue.T(1001));
        for(byte i=0;i<2;i++)for(byte j=0;j<2;j++)Assert.True(q.Enqueue(Flow with{SourcePort=(ushort)(41000+i)},P((byte)(i*2+j)),TemporalPulseQueue.T(1000)));
        Assert.False(q.TryDequeue(TemporalPulseQueue.T(1000),out _));var ids=new List<byte>();
        while(q.TryDequeue(TemporalPulseQueue.T(1001),out var p))ids.Add(p!.Data[0]);
        Assert.Equal(new byte[]{0,2,1,3},ids);Assert.False(q.Enqueue(Flow,P(8),TemporalPulseQueue.T(1001)));
        var flows=new TemporalPulseQueue(mode,TemporalPulseQueue.T(1800));
        for(ushort i=0;i<128;i++)Assert.True(flows.Enqueue(Flow with{SourcePort=i},P(1),TemporalPulseQueue.T(1000)));
        Assert.False(flows.Enqueue(Flow with{SourcePort=129},P(1),TemporalPulseQueue.T(1000)));Assert.Equal(128,flows.Drain().Count());
        var bytes=new TemporalPulseQueue(mode,TemporalPulseQueue.T(1800));
        for(int i=0;i<192;i++)Assert.True(bytes.Enqueue(Flow,P(1,4096),TemporalPulseQueue.T(1000)));
        Assert.False(bytes.Enqueue(Flow,P(1,4096),TemporalPulseQueue.T(1000)));Assert.Equal(192,bytes.Drain().Count());
    }
    [Theory]
    [InlineData(DiagnosticMode.InterPacketGapEqualization)] [InlineData(DiagnosticMode.DeadlineAwareScheduling)]
    public void OverloadMaximumAgeAndMonotonicClamp_NoFuturePacketRequired(DiagnosticMode mode)
    {
        var q=new TemporalPulseQueue(mode,TemporalPulseQueue.T(1800));
        for(int i=0;i<512;i++)Assert.True(q.Enqueue(Flow,P((byte)i),TemporalPulseQueue.T(1000)));
        Assert.False(q.Enqueue(Flow,P(1),TemporalPulseQueue.T(999)));
        int bound=mode==DiagnosticMode.InterPacketGapEqualization?1096:1048;
        int count=0;while(q.TryDequeue(TemporalPulseQueue.T(bound),out _))count++;
        Assert.Equal(512,count);Assert.Equal(0,q.Bytes);Assert.False(q.TryDequeue(TemporalPulseQueue.T(1000),out _));
        var single=new TemporalPulseQueue(mode,TemporalPulseQueue.T(1800));single.Enqueue(Flow,P(9),TemporalPulseQueue.T(1000));
        Assert.True(single.TryDequeue(TemporalPulseQueue.T(bound),out var p));Assert.Equal(9,p!.Data[0]);
    }
    [Theory]
    [InlineData(DiagnosticMode.InterPacketGapEqualization)] [InlineData(DiagnosticMode.DeadlineAwareScheduling)]
    public void EngineMixedProtocolsStopOverflowAndRestart_NoStaleOrModifiedBytes(DiagnosticMode mode)
    {
        using var e=DiagnosticModesTests.Engine();ArmedCaptureTests.ModelArm(e);
        long now=TemporalPulseQueue.T(1000);ArmedCaptureTests.Set(e,"_timestamp",(Func<long>)(()=>now));
        var sent=new List<byte[]>();void Sender()=>e.SendOverride=(p,n,address)=>{Assert.Equal(918273,address.Timestamp);var b=new byte[n];Marshal.Copy(p,b,0,(int)n);sent.Add(b);return true;};Sender();
        var packets=new[]{NetworkTechniquesTests.Packet(),NetworkTechniquesTests.Packet(true),NetworkTechniquesTests.Packet(protocol:6),NetworkTechniquesTests.Packet(true,6)};
        foreach(var p in packets){Assert.False(NetworkTechniquesTests.Capture(e,p));Assert.Equal(p,sent[^1]);}
        Assert.False(e.TriggerArmedEffect(mode,0));Assert.True(e.TriggerArmedEffect(mode,800));
        foreach(var p in packets)Assert.True(NetworkTechniquesTests.Capture(e,p));
        byte[] icmp=NetworkTechniquesTests.Packet();icmp[9]=1;Assert.False(NetworkTechniquesTests.Capture(e,icmp));Assert.Equal(icmp,sent[^1]);
        e.Stop();Assert.Equal(e.TemporalPacketsHeld,e.TemporalPacketsReleased);Assert.Equal(9,sent.Count);
        foreach(var p in packets)Assert.Contains(sent,b=>b.SequenceEqual(p));
        int before=sent.Count;ShapingEffectsTests.Release(e,TemporalPulseQueue.T(9999));Assert.Equal(before,sent.Count);
        ArmedCaptureTests.ModelArm(e);Sender();NetworkTechniquesTests.Capture(e,packets[0]);Assert.Equal(before+1,sent.Count);
        Assert.True(e.TriggerArmedEffect(mode,800));for(int i=0;i<513;i++)NetworkTechniquesTests.Capture(e,packets[0]);
        Assert.False(e.IsEffectActive);Assert.Contains("fail-open",e.LastShapingFallback);Assert.Equal(e.TemporalPacketsHeld,e.TemporalPacketsReleased);Assert.Equal(0,e.PacketsDropped);
    }
    [Theory]
    [InlineData(DiagnosticMode.InterPacketGapEqualization)] [InlineData(DiagnosticMode.DeadlineAwareScheduling)]
    public void SettingsRoundtripAndNoFakeModes(DiagnosticMode mode)=>ConfigurationBehaviorTests.WithVm((vm,e,_,dir)=>
    {
        vm.SelectedOutboundEffect=ModeCatalog.Name(mode);vm.DiagnosticCycleDurationMs=731;vm.SaveSettingsCommand.Execute(null);
        var path=Path.Combine(dir,"settings.json");var bytes=File.ReadAllBytes(path);vm.LoadSettings();Assert.Equal(ModeCatalog.Name(mode),vm.SelectedOutboundEffect);Assert.Equal(731,vm.DiagnosticCycleDurationMs);Assert.Equal(bytes,File.ReadAllBytes(path));Assert.False(e.IsCaptureRequested);
        Assert.Equal(11,vm.DiagnosticModeOptions.Count);Assert.DoesNotContain(vm.DiagnosticModeOptions,n=>n.Contains("UDMUX")||n.Contains("Multipath")||n.Contains("NAT"));
        Assert.Equal("Não avaliado",EffectExperienceCatalog.Rating(mode,250).Label);
        Assert.Equal("Não avaliado",EffectExperienceCatalog.Rating(mode,800).Label);
    },outboundOnly:true);
}
