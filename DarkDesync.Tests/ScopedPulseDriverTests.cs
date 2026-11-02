using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

[Collection("WinDivert controlled driver")]
public sealed class ScopedPulseDriverTests(ITestOutputHelper output)
{
    [DriverTheory, Trait("Category", "Driver")]
    [InlineData(DiagnosticMode.InboundBandwidthShaping)]
    [InlineData(DiagnosticMode.CorrelatedInboundJitter)]
    [InlineData(DiagnosticMode.PacketSizeAwareShaping)]
    [InlineData(DiagnosticMode.AsymmetricDuplexShaping)]
    [InlineData(DiagnosticMode.ControlledPacketReordering)]
    public async Task RealViewModel_ExplicitCompatibleFilter_StartWaits_FlowAutoStarts_PulseStop(DiagnosticMode mode)
    {
        using var peer = await UdpDriverIntegrationTests.Peer.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(IPAddress.Loopback, peer.Port);
        ConfigurationBehaviorTests.WithVm((vm, e, _, _) =>
        {
            void Refresh() => typeof(DarkDesync.ViewModels.MainViewModel).GetMethod("RefreshNetworkDiagnostics",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
            vm.SelectedFilterModeText = vm.FilterModeOptions[4];
            vm.SelectedProcess = new DarkDesync.Models.ProcessInfo { ProcessId = peer.Pid };
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(mode);
            Assert.Equal(vm.FilterModeOptions[4], vm.SelectedFilterModeText);
            int filterIndex = mode is DiagnosticMode.PacketSizeAwareShaping or DiagnosticMode.ControlledPacketReordering ? 0 : 1;
            vm.SelectedFilterModeText = vm.FilterModeOptions[filterIndex];
            Assert.True(e.IsObservingUdp); Assert.False(e.IsCaptureRequested);
            if (mode == DiagnosticMode.ControlledPacketReordering)
            {
                vm.ToggleEngineCommand.Execute(null);
                Assert.False(e.IsCaptureRequested); Assert.Contains("Opt-in", vm.StartStatusText);
                vm.ExperimentalOptIn = true;
            }
            vm.ToggleEngineCommand.Execute(null);
            Assert.True(e.IsCaptureRequested); Assert.False(e.IsRunning);
            Assert.Equal("AGUARDANDO", vm.StatusText); Assert.Equal("STOP", vm.EngineToggleText);
            vm.TriggerCycleCommand.Execute(null); Assert.False(e.IsEffectActive);
            client.Send(new byte[] { 90 }, 1);
            Assert.Equal(90, client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult().Buffer[0]);
            UdpDriverIntegrationTests.WaitUntil(() => e.IsRunning && e.ScopeStatus.UdpScoped > 0).GetAwaiter().GetResult();
            Refresh();
            Assert.True(vm.IsEngineRunning); Assert.Contains("ARMADA", vm.StartStatusText);
            Assert.Equal(vm.FilterModeOptions[filterIndex], vm.SelectedFilterModeText);
            Assert.Contains("udp.SrcPort == " + peer.Port, e.CurrentFilter);
            Assert.Contains("not impostor", e.CurrentFilter);
            Assert.Equal(0, e.PacketsThrottled);
            vm.CycleProfileEnabled = false; vm.TriggerCycleCommand.Execute(null);
            Assert.False(e.IsEffectActive); Assert.Contains("Habilitado", vm.StartStatusText);
            vm.CycleProfileEnabled = true;
            vm.TriggerCycleCommand.Execute(null); Assert.True(e.IsEffectActive);
            client.Send(new byte[900], 900);
            client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            UdpDriverIntegrationTests.WaitUntil(() => !e.IsEffectActive).GetAwaiter().GetResult();
            Assert.True(e.PacketsThrottled > 0); Assert.Equal(0, e.PacketsDropped);
            vm.ToggleEngineCommand.Execute(null);
            Assert.False(e.IsCaptureRequested); Assert.False(e.IsRunning); Assert.Equal("START", vm.EngineToggleText);
        });
        output.WriteLine($"{mode}: actual VM preserved explicit Outbound/AllTraffic PID selection, native FLOW -> NETWORK, disabled effect, pulse, STOP verified on own child loopback.");
    }

    [DriverTheory, Trait("Category", "Driver")]
    [InlineData(DiagnosticMode.InboundBandwidthShaping)]
    [InlineData(DiagnosticMode.CorrelatedInboundJitter)]
    [InlineData(DiagnosticMode.PacketSizeAwareShaping)]
    [InlineData(DiagnosticMode.AsymmetricDuplexShaping)]
    [InlineData(DiagnosticMode.ControlledPacketReordering)]
    public async Task ChildPid_LoopbackOnly_DirectionsScheduleIsolationExpiryStop(DiagnosticMode mode)
    {
        using var peer = await UdpDriverIntegrationTests.Peer.Start();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        client.Connect(IPAddress.Loopback, peer.Port);
        using var e = DiagnosticModesTests.Engine();
        Assert.True(e.ConfigureScopedProfile(mode)); e.ExperimentalReorderingConsent = true;
        e.StartArmed(FilterMode.Custom, peer.Pid, 100, "loopback and udp");
        Assert.True(e.IsCaptureRequested); Assert.True(e.IsObservingUdp);
        await client.SendAsync(new byte[] { 99 }, 1);
        Assert.Equal(99, (await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5))).Buffer[0]);
        await UdpDriverIntegrationTests.WaitUntil(() => e.IsRunning && e.ScopeStatus.UdpScoped > 0);
        Assert.Contains("not impostor", e.CurrentFilter);
        Assert.Contains("udp.SrcPort == " + peer.Port, e.CurrentFilter);
        Assert.DoesNotContain("tcp.SrcPort", e.CurrentFilter); // No TCP tuple is eligible; redundant protocol guard may name TCP.
        await client.SendAsync(new byte[] { 98 }, 1);
        Assert.Equal(98, (await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer[0]);
        Assert.Equal(0, e.PacketsThrottled); // START did not apply anything.
        Assert.True(e.TriggerArmedEffect(mode, 200));
        var clock = Stopwatch.StartNew();
        const int count = 12;
        var ids = new List<byte>(); var times = new List<double>();
        var reader = Read();
        for (byte i = 0; i < count; i++)
        {
            byte[] bytes = new byte[900]; bytes[0] = i;
            await client.SendAsync(bytes, bytes.Length);
        }
        await UdpDriverIntegrationTests.WaitUntil(() => e.PacketsThrottled > 0);
        long before = e.PacketsIntercepted;
        using (var unrelatedRx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var unrelatedTx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            await unrelatedTx.SendAsync(new byte[] { 97 }, (IPEndPoint)unrelatedRx.Client.LocalEndPoint!);
            Assert.Equal(97, (await unrelatedRx.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1))).Buffer[0]);
        }
        // The target may still drain while the other-PID probe runs; its captured
        // tuples are asserted exactly below, rather than assuming no target replies.
        await reader.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(Enumerable.Range(0, count).Select(i => (byte)i), ids.Order());
        if (mode != DiagnosticMode.ControlledPacketReordering)
            Assert.Equal(Enumerable.Range(0, count).Select(i => (byte)i), ids);
        else
        {
            Assert.True(e.IntentionallyReorderedPairs > 0, "Real driver did not perform a controlled swap");
            Assert.False(ids.SequenceEqual(ids.Order()), "Receiver did not observe intentional UDP reordering");
            for (int i = 0; i < count; i++) Assert.InRange(Math.Abs(ids[i] - i), 0, 1);
        }
        await UdpDriverIntegrationTests.WaitUntil(() => e.StateMachine.Current == DesyncStateMachine.State.Armed);
        Assert.True(e.IsRunning); Assert.Equal(0, e.PacketsDropped);
        if (mode is DiagnosticMode.InboundBandwidthShaping or DiagnosticMode.CorrelatedInboundJitter)
        { Assert.True(e.ScopedInboundPacketsHeld > 0); Assert.Equal(0, e.ScopedOutboundPacketsHeld); Assert.True(times[0] >= 20); }
        if (mode == DiagnosticMode.AsymmetricDuplexShaping)
        { Assert.True(e.ScopedInboundPacketsHeld > 0); Assert.True(e.ScopedOutboundPacketsHeld > 0); }
        if (mode is DiagnosticMode.PacketSizeAwareShaping or DiagnosticMode.ControlledPacketReordering)
        { Assert.Equal(0, e.ScopedInboundPacketsHeld); Assert.True(e.ScopedOutboundPacketsHeld > 0); }
        // Unrelated source port is absent from the exact tuple filter. After target
        // traffic has settled, prove unrelated probes do not enter NETWORK at all.
        await Task.Delay(30); before = e.PacketsIntercepted;
        using (var rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var tx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            await tx.SendAsync(new byte[] { 96 }, (IPEndPoint)rx.Client.LocalEndPoint!);
            await rx.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(before, e.PacketsIntercepted);
        await client.SendAsync(new byte[] { 95 }, 1);
        Assert.Equal(95, (await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer[0]);
        long heldBeforeStop = e.PacketsThrottled;
        Assert.True(e.TriggerArmedEffect(mode, 200));
        var pending = client.ReceiveAsync();
        await client.SendAsync(new byte[900], 900);
        await UdpDriverIntegrationTests.WaitUntil(() => e.PacketsThrottled > heldBeforeStop);
        e.Stop(); await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(e.IsRunning); Assert.False(e.IsCaptureRequested);
        await Task.Delay(40); Assert.Equal(0, client.Available); // No duplicate/replay after STOP.
        Assert.Equal(0, e.PacketsDropped);
        output.WriteLine($"{mode}: own child PID={peer.Pid}; native loopback outbound normalized by exact receive ownership; held IN={e.ScopedInboundPacketsHeld}, OUT={e.ScopedOutboundPacketsHeld}; swaps-attempted={e.IntentionallyReorderedPairs}; received [{string.Join(',', ids)}], ms=[{string.Join(',', times.Select(t => t.ToString("F1")))}]; 12/12, no application loss; unrelated PID untouched; STOP complete. Native non-loopback inbound NOT exercised.");

        async Task Read()
        {
            for (int i = 0; i < count; i++)
            {
                var packet = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(900, packet.Buffer.Length); ids.Add(packet.Buffer[0]); times.Add(clock.Elapsed.TotalMilliseconds);
            }
        }
    }
}
