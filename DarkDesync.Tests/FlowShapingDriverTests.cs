using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

[Collection("WinDivert controlled driver")]
public sealed class FlowShapingDriverTests(ITestOutputHelper output)
{
    [GlobalDriverTheory]
    [InlineData(true)]
    [Trait("Category", "Driver")]
    public async Task OwnTwoUdpFlows_SparseFlowIsNotBehindBusyFlow_PerFlowFifo_RealDriver(bool consent)
    {
        Assert.True(consent);
        using var busyRx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sparseRx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var busyTx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sparseTx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        busyTx.Connect((IPEndPoint)busyRx.Client.LocalEndPoint!);
        sparseTx.Connect((IPEndPoint)sparseRx.Client.LocalEndPoint!);
        using var engine = DiagnosticModesTests.Engine();
        engine.UpdateFilter(FilterMode.LegacyGlobalOutbound);
        Assert.True(engine.BeginLegacyGlobalContinuous(DiagnosticMode.None));
        try
        {
            // Warm up the own sockets before measuring the 200ms production pulse.
            await busyTx.SendAsync(new byte[1], 1);
            await busyRx.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(engine.TriggerArmedEffect(DiagnosticMode.FlowAwareShaping, 200));
            var clock = Stopwatch.StartNew();
            var busyTimes = new List<double>();
            var readBusy = ReadBusy();
            var readSparse = sparseRx.ReceiveAsync();
            for (byte id = 0; id < 16; id++)
            {
                byte[] data = new byte[900]; data[0] = id;
                await busyTx.SendAsync(data, data.Length);
            }
            await sparseTx.SendAsync(new byte[900], 900);
            var sparse = await readSparse.WaitAsync(TimeSpan.FromSeconds(3));
            double sparseAt = clock.Elapsed.TotalMilliseconds;
            Assert.Equal(900, sparse.Buffer.Length);
            await readBusy.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(sparseAt >= 70 && sparseAt < 190, $"Sparse flow should use its own byte budget, not wait for busy flow flush: {sparseAt:F1}ms");
            Assert.True(busyTimes[^1] - sparseAt > 15, "Busy queue blocked the sparse flow until pulse end");
            Assert.Equal(0, engine.PacketsDropped);
            Assert.True(engine.IsRunning);
            output.WriteLine($"Flow-Aware real WinDivert: busy 16/16 FIFO, sparse 1/1 at {sparseAt:F1}ms, busy final {busyTimes[^1]:F1}ms; no global-order claim; {engine.ProtocolCounters}");

            async Task ReadBusy()
            {
                for (byte id = 0; id < 16; id++)
                {
                    var packet = await busyRx.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.Equal(id, packet.Buffer[0]); Assert.Equal(900, packet.Buffer.Length);
                    busyTimes.Add(clock.Elapsed.TotalMilliseconds);
                }
            }
        }
        finally { engine.Stop(); }
    }
}
