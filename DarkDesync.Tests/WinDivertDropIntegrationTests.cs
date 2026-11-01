using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public sealed class DriverTheoryAttribute : TheoryAttribute
{
    public DriverTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DARKDESYNC_DRIVER_TESTS") != "1")
            Skip = "Requires Windows administrator and explicit DARKDESYNC_DRIVER_TESTS=1; loopback only.";
    }
}

public class WinDivertDropIntegrationTests
{
    [DriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(12)]
    public async Task RapidStartStop_DrainsWorkersWithoutDisposedToken(int repetitions)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptTcpClientAsync();
        using var engine = DiagnosticModesTests.Engine();
        for (int i = 0; i < repetitions; i++)
        {
            Assert.True(engine.Start(FilterMode.Custom, (uint)Environment.ProcessId, customFilter: "loopback"));
            engine.Stop();
            foreach (var name in new[] { "_captureTask", "_throttleReleaseTask" })
            {
                var worker = (Task)typeof(PacketEngine).GetField(name,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(engine)!;
                Assert.True(worker.IsCompletedSuccessfully, name + ": " + worker.Status);
            }
        }
        Assert.False(engine.IsRunning);
    }

    [DriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    [InlineData(DiagnosticMode.BurstLoss)]
    [InlineData(DiagnosticMode.AsymmetricLoss)]
    public async Task ScopedTcpLoopback_EffectAndRestoration(DiagnosticMode mode)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptTcpClientAsync();
        server.NoDelay = true;
        var local = (IPEndPoint)client.Client.LocalEndPoint!;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        using var engine = DiagnosticModesTests.Engine();
        engine.CycleDropEnabled = false;
        engine.BurstLossPercentage = engine.AsymmetricOutboundLossPercentage = 100;
        string? error = null; engine.ErrorOccurred += e => error = e;
        // Restrict both the PID-owned tuple and this one outgoing loopback direction.
        string filter = $"loopback and outbound and tcp.SrcPort == {local.Port} and tcp.DstPort == {remote.Port}";
        Assert.True(engine.Start(FilterMode.Custom, (uint)Environment.ProcessId, customFilter: filter), error);
        using var controller = new DesyncController(NullLogger<DesyncController>.Instance, engine.StateMachine, engine);
        Assert.True(controller.TriggerCycle(mode, 300));
        byte[] received = new byte[1];
        var receive = server.GetStream().ReadExactlyAsync(received).AsTask();
        var watch = Stopwatch.StartNew();
        await client.GetStream().WriteAsync(new byte[] { 71 });
        await Task.Delay(80);
        Assert.False(receive.IsCompleted, "Target payload bypassed the effect");
        await receive.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(71, received[0]);
        Assert.True(watch.ElapsedMilliseconds >= 250);
        if (mode == DiagnosticMode.OutboundFreeze)
        {
            Assert.True(engine.PacketsThrottled > 0); Assert.Equal(0, engine.PacketsDropped);
        }
        else Assert.True(engine.PacketsDropped > 0);
        // Normal traffic must resume on the same socket without changing the filter.
        await client.GetStream().WriteAsync(new byte[] { 72 });
        await server.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(72, received[0]);
        Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
        engine.Stop(); Assert.False(engine.IsRunning);
    }
}
