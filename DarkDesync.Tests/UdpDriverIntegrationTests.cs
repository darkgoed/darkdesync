using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

// Real driver tests: selected target is a test-only child PID, never Roblox/game traffic.
public sealed class UdpDriverIntegrationTests(ITestOutputHelper output)
{
    [DriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    [InlineData(DiagnosticMode.BurstLoss)]
    [InlineData(DiagnosticMode.AsymmetricLoss)]
    public async Task UdpOnlyChildPid_FlowScopedEffect_OtherPidUntouched_AndRestore(DiagnosticMode mode)
    {
        using var peer = await Peer.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(IPAddress.Loopback, peer.Port);
        using var engine = DiagnosticModesTests.Engine();
        // The child has UDP only. No fallback to the test runner's TCP connections.
        Assert.False(engine.Start(FilterMode.Custom, peer.Pid, customFilter: "outbound and udp"));
        Assert.True(engine.IsObservingUdp, "FLOW handle did not open (driver/permission/filter error)");
        Assert.False(engine.IsRunning); Assert.Equal("false", engine.CurrentFilter);
        await client.SendAsync(new byte[] { 60 }, 1);
        Assert.Equal(new byte[] { 60 }, (await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
        await WaitUntil(() => engine.ObservedUdpFlowCount > 0);
        Assert.True(engine.Start(FilterMode.Custom, peer.Pid, customFilter: "outbound and udp"));
        output.WriteLine($"PID={peer.Pid}; UDP-only filter: {engine.CurrentFilter}");
        Assert.Contains("udp.SrcPort == " + peer.Port, engine.CurrentFilter);
        Assert.DoesNotContain("tcp", engine.CurrentFilter);
        engine.BurstLossPercentage = engine.AsymmetricOutboundLossPercentage = 100;
        engine.AsymmetricInboundLossPercentage = 0;
        using var controller = new DesyncController(NullLogger<DesyncController>.Instance, engine.StateMachine, engine);
        var clock = Stopwatch.StartNew();
        Assert.True(controller.TriggerCycle(mode, 800));
        var receive = client.ReceiveAsync();
        await client.SendAsync(new byte[] { 61 }, 1);
        await WaitUntil(() => engine.PacketsDropped + engine.PacketsThrottled > 0);
        output.WriteLine($"At effect assertion: elapsed={clock.ElapsedMilliseconds}ms, state={controller.CurrentState}, held={engine.PacketsThrottled}, dropped={engine.PacketsDropped}, sent={engine.PacketsPassedThrough}");
        Assert.False(receive.IsCompleted, $"Target reply bypassed the UDP effect; elapsed={clock.ElapsedMilliseconds}ms, state={controller.CurrentState}");
        long intercepted = engine.PacketsIntercepted;
        Assert.Equal(DesyncStateMachine.State.Active, controller.CurrentState);
        // Another PID owns BOTH ends of this probe. It must not enter the target filter.
        using (var otherReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var otherSender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            await otherSender.SendAsync(new byte[] { 99 }, (IPEndPoint)otherReceiver.Client.LocalEndPoint!);
            Assert.Equal(new byte[] { 99 }, (await otherReceiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1))).Buffer);
        }
        Assert.Equal(DesyncStateMachine.State.Active, controller.CurrentState);
        Assert.Equal(intercepted, engine.PacketsIntercepted);
        controller.CancelCycle();
        if (mode == DiagnosticMode.OutboundFreeze)
        {
            Assert.Equal(new byte[] { 61 }, (await receive.WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
            Assert.Equal(0, engine.PacketsDropped);
            receive = client.ReceiveAsync();
        }
        else Assert.True(engine.PacketsDropped > 0);
        // UDP has no retransmission: losses are irreversible, only new traffic is restored.
        await client.SendAsync(new byte[] { 62 }, 1);
        Assert.Equal(new byte[] { 62 }, (await receive.WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        engine.Stop(); Assert.False(engine.IsRunning); Assert.False(engine.IsObservingUdp);
    }

    [DriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(true)]
    public async Task UdpSharedBindingAcrossRealPids_RefusesPayloadCapture(bool shared)
    {
        using var peer = await Peer.Start(shared);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(IPAddress.Loopback, peer.Port);
        using var engine = DiagnosticModesTests.Engine();
        Assert.False(engine.Start(FilterMode.Custom, peer.Pid, customFilter: "udp"));
        Assert.True(engine.IsObservingUdp);
        await client.SendAsync(new byte[] { 70 }, 1);
        Assert.Equal(new byte[] { 70 }, (await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
        await WaitUntil(() => engine.ObservedUdpFlowCount > 0);
        using var conflict = new UdpClient(AddressFamily.InterNetwork);
        conflict.ExclusiveAddressUse = false;
        conflict.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        conflict.Client.Bind(new IPEndPoint(IPAddress.Loopback, peer.Port));
        var flow = new ProcessNetworkScanner.ProcessEndpoint(17, (ushort)peer.Port,
            (ushort)((IPEndPoint)client.Client.LocalEndPoint!).Port, 0x0100007f, 0x0100007f);
        var scanner = new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance);
        Assert.False(scanner.IsExclusiveUdpBinding(peer.Pid, flow));
        Assert.False(engine.Start(FilterMode.Custom, peer.Pid, customFilter: "udp"));
        Assert.Equal("false", engine.CurrentFilter); Assert.False(engine.IsRunning);
        engine.Stop(); Assert.False(engine.IsObservingUdp);
        output.WriteLine("Shared UDP local port across child/test-host PIDs was refused with no payload handle.");
    }

    private static async Task WaitUntil(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(ready(), "Timed out waiting for actual driver FLOW/packet evidence");
    }

    private sealed class Peer(Process process, int port) : IDisposable
    {
        public int Port { get; } = port;
        public uint Pid => (uint)process.Id;
        public static async Task<Peer> Start(bool shared = false)
        {
            string dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../DarkDesync.UiTests/bin/Release/net8.0-windows/DarkDesync.UiTests.dll"));
            Assert.True(File.Exists(dll), "Build DarkDesync.UiTests before driver tests: " + dll);
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (string arg in new[] { dll, "--udp-peer", "0", shared ? "shared" : "exclusive" }) start.ArgumentList.Add(arg);
            var child = Process.Start(start)!;
            try
            {
                string? line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(int.TryParse(line, out int boundPort) && boundPort > 0, "UDP peer did not report its bound port: " + line);
                return new Peer(child, boundPort);
            }
            catch { if (!child.HasExited) child.Kill(true); child.Dispose(); throw; }
        }
        public void Dispose()
        {
            try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); } }
            finally { process.Dispose(); }
        }
    }
}
