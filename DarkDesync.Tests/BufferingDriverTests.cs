using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

[Collection("WinDivert controlled driver")]
public sealed class BufferingDriverTests(ITestOutputHelper output)
{
    [DriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    [InlineData(DiagnosticMode.HoldAndDrainShaping)]
    [InlineData(DiagnosticMode.RateStepShaping)]
    [InlineData(DiagnosticMode.DualTokenBucket)]
    [InlineData(DiagnosticMode.BandwidthShaping)]
    [InlineData(DiagnosticMode.FlowAwareShaping)]
    public async Task OwnUdp_MeasuresReleaseProfile_AndOwnTcp_DeliversExactStream(DiagnosticMode mode)
    {
        using var engine = DiagnosticModesTests.Engine();
        uint pid = (uint)Environment.ProcessId;
        engine.ObserveTarget(pid); // Observe BEFORE our first UDP FLOW event.
        using var udpRx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udpTx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        udpTx.Connect((IPEndPoint)udpRx.Client.LocalEndPoint!);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var tcpTx = new TcpClient { NoDelay = true };
            var accept = listener.AcceptTcpClientAsync();
            await tcpTx.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using var tcpRx = await accept;
            tcpRx.NoDelay = true;
            int udpSource = ((IPEndPoint)udpTx.Client.LocalEndPoint!).Port;
            int udpDestination = ((IPEndPoint)udpRx.Client.LocalEndPoint!).Port;
            int tcpSource = ((IPEndPoint)tcpTx.Client.LocalEndPoint!).Port;
            int tcpDestination = ((IPEndPoint)listener.LocalEndpoint).Port;
            string ownTraffic = $"outbound and ((udp and udp.SrcPort == {udpSource} and udp.DstPort == {udpDestination}) or (tcp and tcp.SrcPort == {tcpSource} and tcp.DstPort == {tcpDestination}))";
            Assert.True(engine.StartArmed(FilterMode.Custom, pid, 0, ownTraffic));
            try
            {
                await udpTx.SendAsync(new byte[] { 255 }, 1);
                Assert.Equal(new byte[] { 255 }, (await udpRx.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
                await Until(() => engine.IsRunning && engine.ScopeStatus.UdpScoped > 0);
                // The bandwidth experiment excludes runner HTTPS/control traffic by
                // proven PID/tuples AND this own-socket custom filter. GLOBAL lifecycle
                // and literal outbound remain tested separately by MinimalEffectsDriverTests.
                await Task.Delay(30);
                Assert.Contains($"udp.SrcPort == {udpSource}", engine.CurrentFilter);
                Assert.Equal(0, engine.PacketsThrottled);
                long held = engine.UdpPacketsHeld;
                var clock = Stopwatch.StartNew();
                Assert.True(engine.TriggerArmedEffect(mode, 200));
                // Identical own 900-byte UDP payload load for every mode.
                await Task.Delay(20);
                var receivedAt = new List<double>();
                async Task ReceiveAll()
                {
                    for (int id = 0; id < 16; id++)
                    {
                        var packet = await udpRx.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                        receivedAt.Add(clock.Elapsed.TotalMilliseconds);
                        Assert.Equal(900, packet.Buffer.Length);
                        Assert.Equal(id, packet.Buffer[0]);
                        Assert.All(packet.Buffer.Skip(1), b => Assert.Equal((byte)id, b));
                    }
                }
                var receive = ReceiveAll();
                for (byte id = 0; id < 16; id++)
                    await udpTx.SendAsync(Enumerable.Repeat(id, 900).ToArray(), 900);
                await receive;
                await Until(() => engine.StateMachine.Current == DesyncStateMachine.State.Armed);
                Assert.True(engine.UdpPacketsHeld > held);
                Assert.Equal(0, engine.PacketsDropped);
                // Wide lower bounds tolerate the Windows worker clock, but reject
                // pass-through or modes that merely hold everything to pulse end.
                double minimum = mode switch
                {
                    DiagnosticMode.OutboundFreeze => 150,
                    DiagnosticMode.HoldAndDrainShaping => 65,
                    DiagnosticMode.RateStepShaping => 80,
                    DiagnosticMode.FlowAwareShaping => 80,
                    DiagnosticMode.BandwidthShaping => 45,
                    _ => 20
                };
                string evidence = $"{mode}; ms=[{string.Join(",", receivedAt.Select(t => t.ToString("F1")))}]; fallback={engine.LastShapingFallback ?? "none"}";
                output.WriteLine(evidence);
                Assert.True(receivedAt[0] >= minimum, $"Released before byte/phase budget: {evidence}");
                if (mode != DiagnosticMode.OutboundFreeze)
                {
                    Assert.True(receivedAt[0] < 195, $"Full-pulse Freeze: {evidence}");
                    Assert.True(receivedAt[^1] - receivedAt[0] > 25, $"Did not pace controlled burst: {evidence}");
                }
                output.WriteLine($"{mode}: UDP 16/16 FIFO, reinjection-observed receive ms=[{string.Join(",", receivedAt.Select(t => t.ToString("F1")))}], span={receivedAt[^1] - receivedAt[0]:F1}ms; {engine.ProtocolCounters}");

                // New pulse must reset byte budget/occupancy. TCP is established before capture.
                byte[] expected = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
                byte[] actual = new byte[expected.Length];
                long tcpHeld = engine.PacketsThrottled; // No UDP is sent during this phase.
                Assert.True(engine.TriggerArmedEffect(mode, 200));
                await Task.Delay(20);
                // Keep the controlled IP packets below the bounded 1536-byte burst.
                // Await each own read to prevent loopback offload coalescing the whole stream.
                for (int offset = 0; offset < expected.Length; offset += 900)
                {
                    int size = Math.Min(900, expected.Length - offset);
                    var read = tcpRx.GetStream().ReadExactlyAsync(actual.AsMemory(offset, size)).AsTask();
                    await tcpTx.GetStream().WriteAsync(expected.AsMemory(offset, size));
                    await read.WaitAsync(TimeSpan.FromSeconds(5));
                }
                Assert.Equal(expected, actual);
                await Until(() => engine.StateMachine.Current == DesyncStateMachine.State.Armed);
                Assert.True(engine.PacketsThrottled > tcpHeld);
                Assert.Equal(0, engine.PacketsDropped);
                long after = engine.PacketsThrottled;
                await udpTx.SendAsync(new byte[] { 254 }, 1);
                Assert.Equal(new byte[] { 254 }, (await udpRx.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
                Assert.Equal(after, engine.PacketsThrottled);
                output.WriteLine($"{mode}: TCP exact stream 8192/8192 bytes; post-pulse pass-through; {engine.ProtocolCounters}");
            }
            finally { engine.Stop(); }
            Assert.False(engine.IsCaptureRequested);
            Assert.False(engine.IsRunning);
        }
        finally { listener.Stop(); }
    }

    private static async Task Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(2);
        Assert.True(ready(), "Driver pulse did not restore to armed state");
    }
}
