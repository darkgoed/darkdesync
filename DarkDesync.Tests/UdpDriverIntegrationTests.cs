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
[Collection("WinDivert controlled driver")]
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
        await WaitUntil(() => engine.ScopeStatus.UdpScoped > 0 && engine.IsRunning);
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
        engine.Stop(); Assert.False(engine.IsRunning); Assert.True(engine.IsObservingUdp);
        Assert.True(engine.Start(FilterMode.Custom, peer.Pid, customFilter: "outbound and udp"));
        Assert.True(engine.ScopeStatus.UdpScoped > 0); // Restart uses preserved evidence, no reconnect.
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
        await WaitUntil(() => engine.ScopeStatus.UdpScoped == 0 && !engine.IsRunning && engine.ScopeStatus.UdpObserved > 0);
        Assert.Equal("false", engine.CurrentFilter); Assert.False(engine.IsRunning);
        Assert.True(engine.ScopeStatus.UdpObserved > 0); // Refusal must not erase observation.
        engine.Stop(); Assert.True(engine.IsObservingUdp);
        output.WriteLine("Shared UDP local port across child/test-host PIDs was refused with no payload handle.");
    }

    [DriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(443)]
    public async Task Tcp443First_LateUdpAutoAdded_RestartPreservesLedger_CloseRevokesWithoutDeadlock(int tcpPort)
    {
        using var tcpListener = new TcpListener(IPAddress.Loopback, tcpPort);
        tcpListener.Start(); // Exact controlled TCP:443, not a remote/public HTTPS service.
        using var peer = await Peer.Start(tcpPort: tcpPort);
        using var tcpServer = await tcpListener.AcceptTcpClientAsync();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(IPAddress.Loopback, peer.Port);
        using var engine = DiagnosticModesTests.Engine();
        engine.ObserveTarget(peer.Pid);
        Assert.True(engine.IsObservingUdp); Assert.False(engine.IsRunning);
        Assert.True(engine.Start(FilterMode.Outbound, peer.Pid));
        Assert.True(engine.ScopeStatus.TcpScoped > 0); Assert.Equal(0, engine.ScopeStatus.UdpScoped);
        Assert.Contains("tcp.DstPort == 443", engine.CurrentFilter);
        Assert.DoesNotContain("udp", engine.CurrentFilter);
        Assert.Contains("Somente TCP", engine.ScopeStatus.Reason);

        // The UDP flow did not exist when the TCP-only NETWORK handle was opened.
        await client.SendAsync(new byte[] { 80 }, 1);
        Assert.Equal(new byte[] { 80 }, (await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer);
        await WaitUntil(() => engine.ScopeStatus.UdpScoped > 0);
        Assert.Contains("udp.SrcPort == " + peer.Port, engine.CurrentFilter);
        Assert.Contains("tcp.DstPort == 443", engine.CurrentFilter);
        Assert.True(engine.ScopeStatus.TcpScoped > 0);
        output.WriteLine("TCP:443 → late UDP promoted automatically: " + engine.CurrentFilter);

        // No stall, duplication or lost datagrams during repeated filter turnover.
        for (int i = 0; i < 6; i++)
        {
            var send = client.SendAsync(new byte[] { (byte)(90 + i) }, 1);
            engine.UpdateFilter(i % 2 == 0 ? FilterMode.AllTraffic : FilterMode.Outbound, peer.Pid);
            await send;
            Assert.Equal(new byte[] { (byte)(90 + i) }, (await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
            Assert.False(client.Available > 0, "Duplicate packet after handle turnover");
            Assert.True(engine.ScopeStatus.UdpScoped > 0); Assert.True(engine.IsObservingUdp);
        }
        engine.Stop(); Assert.True(engine.IsObservingUdp); Assert.True(engine.ObservedUdpFlowCount > 0);
        Assert.True(engine.Start(FilterMode.Outbound, peer.Pid)); Assert.True(engine.ScopeStatus.UdpScoped > 0);

        engine.BurstLossPercentage = 100;
        using var controller = new DesyncController(NullLogger<DesyncController>.Instance, engine.StateMachine, engine);
        Assert.True(controller.TriggerCycle(DiagnosticMode.BurstLoss, 800));
        var receive = client.ReceiveAsync();
        await client.SendAsync(new byte[] { 81 }, 1);
        await WaitUntil(() => engine.UdpPacketsDropped > 0);
        Assert.False(receive.IsCompleted);
        Assert.Equal(DesyncStateMachine.State.Active, controller.CurrentState);
        long beforeUnrelatedProbe = engine.PacketsIntercepted;
        using (var unrelatedReceiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var unrelatedSender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            await unrelatedSender.SendAsync(new byte[] { 82 }, (IPEndPoint)unrelatedReceiver.Client.LocalEndPoint!);
            Assert.Equal(new byte[] { 82 }, (await unrelatedReceiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1))).Buffer);
        }
        Assert.Equal(DesyncStateMachine.State.Active, controller.CurrentState);
        Assert.Equal(beforeUnrelatedProbe, engine.PacketsIntercepted); // Other PID must not even enter NETWORK scope.
        controller.CancelCycle();
        await client.SendAsync(new byte[] { 83 }, 1);
        Assert.Equal(new byte[] { 83 }, (await receive.WaitAsync(TimeSpan.FromSeconds(3))).Buffer);

        await peer.CloseUdp(); // Same PID and TCP survive: revoke UDP, not the entire ledger/PID.
        await WaitUntil(() => engine.ScopeStatus.UdpScoped == 0 && engine.ScopeStatus.TcpScoped > 0);
        Assert.DoesNotContain("udp", engine.CurrentFilter); Assert.True(engine.IsRunning);
        peer.Kill();
        await WaitUntil(() => !engine.IsRunning && !engine.IsObservingUdp && engine.ScopeStatus.ProcessId == 0);
        Assert.Equal("false", engine.CurrentFilter);
        output.WriteLine("UDP socket closure revoked UDP only; PID closure revoked all scope; workers terminated without deadlock.");
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
        public async Task CloseUdp()
        {
            await process.StandardInput.WriteLineAsync("close-udp");
            await process.StandardInput.FlushAsync();
            Assert.Equal("udp-closed", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        public void Kill() { if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); } }
        public static async Task<Peer> Start(bool shared = false, int? tcpPort = null)
        {
            string dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../DarkDesync.UiTests/bin/Release/net8.0-windows/DarkDesync.UiTests.dll"));
            Assert.True(File.Exists(dll), "Build DarkDesync.UiTests before driver tests: " + dll);
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true
            };
            foreach (string arg in new[] { dll, "--udp-peer", "0", shared ? "shared" : "exclusive" }) start.ArgumentList.Add(arg);
            if (tcpPort.HasValue) start.ArgumentList.Add(tcpPort.Value.ToString());
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
