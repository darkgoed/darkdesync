using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

// Never uses Outbound/global filters or a remote destination.
public sealed class PacketEngineLoopbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ContinuousThrottle_CustomLoopback_HoldsIdleAndFlushesWhenDisabled()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int sourcePort = ((IPEndPoint)sender.Client.LocalEndPoint!).Port;
        var target = (IPEndPoint)receiver.Client.LocalEndPoint!;
        string filter = $"loopback and ip and udp and ip.SrcAddr == 127.0.0.1 and ip.DstAddr == 127.0.0.1 and udp.SrcPort == {sourcePort} and udp.DstPort == {target.Port}";
        var state = new DesyncStateMachine();
        using var engine = new PacketEngine(NullLogger<PacketEngine>.Instance,
            new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance),
            new FilterBuilder(NullLogger<FilterBuilder>.Instance), new FlowTracker(NullLogger<FlowTracker>.Instance), state)
        { CycleDropEnabled = false, ThrottleEnabled = true, ThrottleContinuous = true, ThrottleTimeframeMs = 300, ThrottleJitterMs = 25 };
        output.WriteLine($"Isolated continuous filter: {filter}");
        Assert.True(engine.Start(FilterMode.Custom, customFilter: filter));
        Assert.Equal(DesyncStateMachine.State.Idle, state.Current);
        var watch = Stopwatch.StartNew();
        await sender.SendAsync(new byte[] { 71 }, target);
        var received = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(4));
        output.WriteLine($"Continuous in Idle: {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(new byte[] { 71 }, received.Buffer); Assert.True(watch.ElapsedMilliseconds >= 260);
        engine.ThrottleTimeframeMs = 2000;
        await sender.SendAsync(new byte[] { 72 }, target);
        var deadline = Stopwatch.StartNew();
        while (engine.PacketsThrottled < 2 && deadline.ElapsedMilliseconds < 2000) await Task.Delay(5);
        Assert.True(engine.PacketsThrottled >= 2);
        Assert.True(state.TryActivate()); state.ForceIdle();
        var receive = receiver.ReceiveAsync(CancellationToken.None).AsTask();
        Assert.NotSame(receive, await Task.WhenAny(receive, Task.Delay(100)));
        watch.Restart(); engine.ThrottleEnabled = false;
        received = await receive.WaitAsync(TimeSpan.FromSeconds(1));
        output.WriteLine($"Continuous disable flush: {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(new byte[] { 72 }, received.Buffer); Assert.Equal(0, engine.PacketsDropped);
        engine.Stop(); Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task CustomEphemeralLoopback_DefaultDrop100_ThrottleJitterAndRestoreDeliver()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int sourcePort = ((IPEndPoint)sender.Client.LocalEndPoint!).Port;
        var target = (IPEndPoint)receiver.Client.LocalEndPoint!;
        string filter = $"loopback and ip and udp and ip.SrcAddr == 127.0.0.1 and ip.DstAddr == 127.0.0.1 and udp.SrcPort == {sourcePort} and udp.DstPort == {target.Port}";
        var state = new DesyncStateMachine();
        using var engine = new PacketEngine(NullLogger<PacketEngine>.Instance,
            new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance),
            new FilterBuilder(NullLogger<FilterBuilder>.Instance),
            new FlowTracker(NullLogger<FlowTracker>.Instance), state)
        {
            ThrottleEnabled = true,
            ThrottleTimeframeMs = 400,
            ThrottleJitterMs = 50,
            ThrottleChance = 100
        };
        string? error = null;
        engine.ErrorOccurred += message => error = message;
        output.WriteLine($"Isolated filter: {filter}");
        Assert.True(engine.Start(FilterMode.Custom, customFilter: filter), error ?? "WinDivert could not start");
        Assert.True(state.TryActivate());
        var watch = Stopwatch.StartNew();
        await sender.SendAsync(new byte[] { 11, 22, 33 }, target);
        var received = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(4));
        watch.Stop();
        output.WriteLine($"Throttle+jitter delivery: {watch.Elapsed.TotalMilliseconds:F1} ms; queued={engine.PacketsThrottled}, dropped={engine.PacketsDropped}, sent={engine.PacketsPassedThrough}");
        Assert.Equal(new byte[] { 11, 22, 33 }, received.Buffer);
        Assert.True(watch.Elapsed.TotalMilliseconds >= 340, "Packet bypassed the 400ms +/-50ms hold");
        Assert.True(engine.PacketsThrottled >= 1);
        Assert.Equal(0, engine.PacketsDropped);

        engine.ThrottleTimeframeMs = 2000;
        await sender.SendAsync(new byte[] { 44 }, target);
        var deadline = Stopwatch.StartNew();
        while (engine.PacketsThrottled < 2 && deadline.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(5);
        Assert.True(engine.PacketsThrottled >= 2, "Restore packet was not captured");
        watch.Restart();
        Assert.True(state.TryBeginRestore());
        received = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1));
        watch.Stop();
        output.WriteLine($"Restore flush delivery: {watch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(new byte[] { 44 }, received.Buffer);
        Assert.Equal(0, engine.PacketsDropped);
        engine.Stop();
        Assert.False(engine.IsRunning);
    }
}
