using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using DarkDesync.Core;
using DarkDesync.Models;
using DarkDesync.Services;
using DarkDesync.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public sealed class DiagnosticModesTests
{
    private static (PacketEngine engine, DesyncStateMachine state, DesyncController controller) CreateTestHarness()
    {
        var state = new DesyncStateMachine();
        var scanner = new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance);
        var filterBuilder = new FilterBuilder(NullLogger<FilterBuilder>.Instance);
        var flowTracker = new FlowTracker(NullLogger<FlowTracker>.Instance);
        var engine = new PacketEngine(NullLogger<PacketEngine>.Instance, scanner, filterBuilder, flowTracker, state);
        engine.SendOverride = (buf, len, addr) => true;
        var controller = new DesyncController(NullLogger<DesyncController>.Instance, state, engine);
        return (engine, state, controller);
    }

    private static IntPtr CreateMockIpPacket(string srcIp, string dstIp, ushort srcPort, ushort dstPort, byte protocol = 17)
    {
        // Minimal IPv4 + UDP packet header (20 bytes IP + 8 bytes UDP = 28 bytes)
        byte[] packet = new byte[28];
        packet[0] = 0x45; // IPv4, IHL = 5 (20 bytes)
        packet[8] = 64;   // TTL
        packet[9] = protocol; // UDP=17, TCP=6

        byte[] srcBytes = IPAddress.Parse(srcIp).GetAddressBytes();
        byte[] dstBytes = IPAddress.Parse(dstIp).GetAddressBytes();
        Array.Copy(srcBytes, 0, packet, 12, 4);
        Array.Copy(dstBytes, 0, packet, 16, 4);

        packet[20] = (byte)(srcPort >> 8);
        packet[21] = (byte)(srcPort & 0xFF);
        packet[22] = (byte)(dstPort >> 8);
        packet[23] = (byte)(dstPort & 0xFF);
        packet[24] = 0; // Length
        packet[25] = 8;

        IntPtr ptr = Marshal.AllocHGlobal(packet.Length);
        Marshal.Copy(packet, 0, ptr, packet.Length);
        return ptr;
    }

    [Fact]
    public void OutboundFreeze_InterruptsOutboundPackets_PassesInboundPackets()
    {
        var (engine, state, controller) = CreateTestHarness();
        using (engine)
        using (controller)
        {
            engine.ActiveDiagnosticMode = DiagnosticMode.OutboundFreeze;
            engine.AuthorizedEndpoint = "127.0.0.1";
            state.TryActivate();

            IntPtr packet = CreateMockIpPacket("127.0.0.1", "127.0.0.1", 5000, 6000);
            try
            {
                // Outbound packet: must be intercepted and frozen in throttle queue
                var outAddr = new WinDivertNative.Address { Flags = (1 << 17) | (1 << 18) }; // Outbound + Loopback
                bool outResult = engine.ProcessCapturedPacket(packet, 28, outAddr, new Random());
                Assert.True(outResult);
                Assert.Equal(1, engine.PacketsThrottled);
                Assert.Equal(0, engine.PacketsDropped);

                // Inbound packet: must NOT be frozen; passes through
                var inAddr = new WinDivertNative.Address { Flags = (1 << 18) }; // Inbound + Loopback (bit 17 = 0)
                bool inResult = engine.ProcessCapturedPacket(packet, 28, inAddr, new Random());
                Assert.False(inResult);
                Assert.Equal(1, engine.PacketsThrottled);
            }
            finally
            {
                Marshal.FreeHGlobal(packet);
            }
        }
    }

    [Fact]
    public void OutboundFreeze_DropMode_DropsOutboundPackets()
    {
        var (engine, state, controller) = CreateTestHarness();
        using (engine)
        using (controller)
        {
            engine.ActiveDiagnosticMode = DiagnosticMode.OutboundFreeze;
            engine.FreezeDropsPackets = true;
            engine.AuthorizedEndpoint = "127.0.0.1";
            state.TryActivate();

            IntPtr packet = CreateMockIpPacket("127.0.0.1", "127.0.0.1", 5000, 6000);
            try
            {
                var outAddr = new WinDivertNative.Address { Flags = (1 << 17) | (1 << 18) };
                bool outResult = engine.ProcessCapturedPacket(packet, 28, outAddr, new Random());
                Assert.True(outResult);
                Assert.Equal(1, engine.PacketsDropped);
                Assert.Equal(0, engine.PacketsThrottled);
            }
            finally
            {
                Marshal.FreeHGlobal(packet);
            }
        }
    }

    [Fact]
    public void BurstLoss_SimulatesIntermittentLossWithConfiguredPercentage()
    {
        var (engine, state, controller) = CreateTestHarness();
        using (engine)
        using (controller)
        {
            engine.ActiveDiagnosticMode = DiagnosticMode.BurstLoss;
            engine.BurstLossPercentage = 100; // 100% loss during burst
            engine.AuthorizedEndpoint = "127.0.0.1";
            state.TryActivate();

            IntPtr packet = CreateMockIpPacket("127.0.0.1", "127.0.0.1", 5000, 6000);
            try
            {
                var addr = new WinDivertNative.Address { Flags = (1 << 17) | (1 << 18) };
                bool dropResult = engine.ProcessCapturedPacket(packet, 28, addr, new Random());
                Assert.True(dropResult);
                Assert.Equal(1, engine.PacketsDropped);

                // With 0% burst loss, packet should pass
                engine.BurstLossPercentage = 0;
                bool passResult = engine.ProcessCapturedPacket(packet, 28, addr, new Random());
                Assert.False(passResult);
                Assert.Equal(1, engine.PacketsDropped); // Unchanged
            }
            finally
            {
                Marshal.FreeHGlobal(packet);
            }
        }
    }

    [Fact]
    public void AsymmetricLoss_IndependentInboundAndOutboundLossRates()
    {
        var (engine, state, controller) = CreateTestHarness();
        using (engine)
        using (controller)
        {
            engine.ActiveDiagnosticMode = DiagnosticMode.AsymmetricLoss;
            engine.AsymmetricInboundLossPercentage = 0;   // 0% Inbound
            engine.AsymmetricOutboundLossPercentage = 100; // 100% Outbound
            engine.AuthorizedEndpoint = "127.0.0.1";
            state.TryActivate();

            IntPtr packet = CreateMockIpPacket("127.0.0.1", "127.0.0.1", 5000, 6000);
            try
            {
                // Outbound packet: must be dropped (100%)
                var outAddr = new WinDivertNative.Address { Flags = (1 << 17) | (1 << 18) };
                bool outResult = engine.ProcessCapturedPacket(packet, 28, outAddr, new Random());
                Assert.True(outResult);
                Assert.Equal(1, engine.PacketsDropped);

                // Inbound packet: must pass through (0%)
                var inAddr = new WinDivertNative.Address { Flags = (1 << 18) }; // Inbound (Outbound bit not set)
                bool inResult = engine.ProcessCapturedPacket(packet, 28, inAddr, new Random());
                Assert.False(inResult);
                Assert.Equal(1, engine.PacketsDropped); // Count remains 1
            }
            finally
            {
                Marshal.FreeHGlobal(packet);
            }
        }
    }

    [Fact]
    public void Safety_DiagnosticRestrictedToAuthorizedTraffic_IgnoresThirdParty()
    {
        var (engine, state, controller) = CreateTestHarness();
        using (engine)
        using (controller)
        {
            engine.ActiveDiagnosticMode = DiagnosticMode.BurstLoss;
            engine.BurstLossPercentage = 100;
            engine.AuthorizedEndpoint = "192.168.1.100"; // Test server under developer control
            state.TryActivate();

            // 1. Authorized test packet: matches authorized IP -> dropped
            IntPtr authPacket = CreateMockIpPacket("192.168.1.5", "192.168.1.100", 5000, 8080);
            // 2. Third-party traffic (e.g. Game/Discord/Browser to 1.1.1.1) -> NOT authorized -> MUST PASS
            IntPtr thirdPartyPacket = CreateMockIpPacket("192.168.1.5", "1.1.1.1", 5000, 443);

            try
            {
                var addr = new WinDivertNative.Address { Flags = 1 << 17 }; // Outbound, non-loopback
                Assert.True(engine.IsAuthorizedTraffic(authPacket, 28, addr));
                Assert.False(engine.IsAuthorizedTraffic(thirdPartyPacket, 28, addr));

                // Process authorized packet -> dropped
                bool authResult = engine.ProcessCapturedPacket(authPacket, 28, addr, new Random());
                Assert.True(authResult);
                Assert.Equal(1, engine.PacketsDropped);

                // Process third-party packet -> passed through immediately, NEVER dropped
                bool thirdPartyResult = engine.ProcessCapturedPacket(thirdPartyPacket, 28, addr, new Random());
                Assert.False(thirdPartyResult);
                Assert.Equal(1, engine.PacketsDropped);
            }
            finally
            {
                Marshal.FreeHGlobal(authPacket);
                Marshal.FreeHGlobal(thirdPartyPacket);
            }
        }
    }

    [Fact]
    public async Task DiagnosticCycle_EnforcesDurationRange100To200Ms_AndRestoresAutomatically()
    {
        var (engine, state, controller) = CreateTestHarness();
        using (engine)
        using (controller)
        {
            CycleRecord? recorded = null;
            controller.CycleFinished += r => recorded = r;

            // Trigger with 150 ms default
            bool started = controller.TriggerDiagnosticCycle(DiagnosticMode.BurstLoss, 150);
            Assert.True(started);
            Assert.Equal(DesyncStateMachine.State.Active, controller.CurrentState);
            Assert.Equal(DiagnosticMode.BurstLoss, engine.ActiveDiagnosticMode);

            // Wait for cycle to complete + automatic restoration
            await Task.Delay(260);

            Assert.Equal(DesyncStateMachine.State.Idle, controller.CurrentState);
            Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
            Assert.NotNull(recorded);
            Assert.Equal("Burst Loss", recorded.ProfileName);
            Assert.True(recorded.CompletedNormally);
            Assert.InRange(recorded.DurationMs, 90, 250);
        }
    }

    [Fact]
    public async Task DiagnosticCycle_ClampsDurationBelow100AndAbove200()
    {
        var (engine, state, controller) = CreateTestHarness();
        using (engine)
        using (controller)
        {
            CycleRecord? recorded = null;
            controller.CycleFinished += r => recorded = r;

            // Duration below 100 clamps to 100 ms
            Assert.True(controller.TriggerDiagnosticCycle(DiagnosticMode.OutboundFreeze, 10));
            await Task.Delay(200);

            Assert.Equal(DesyncStateMachine.State.Idle, controller.CurrentState);
            Assert.NotNull(recorded);
            Assert.Equal("Outbound Freeze", recorded.ProfileName);
        }
    }

    [Fact]
    public void FilterBuilder_BuildsAuthorizedFilterCorrectly()
    {
        var builder = new FilterBuilder(NullLogger<FilterBuilder>.Instance);

        // Loopback / 127.0.0.1
        string loopFilter = builder.BuildAuthorizedFilter("127.0.0.1");
        Assert.Contains("loopback", loopFilter);
        Assert.Contains("127.0.0.1", loopFilter);

        // Specific IP
        string ipFilter = builder.BuildAuthorizedFilter("192.168.1.50");
        Assert.Contains("192.168.1.50", ipFilter);

        // Specific IP:Port
        string endpointFilter = builder.BuildAuthorizedFilter("10.0.0.1:8080");
        Assert.Contains("10.0.0.1", endpointFilter);
        Assert.Contains("8080", endpointFilter);

        // With process ports
        string procFilter = builder.BuildAuthorizedFilter("127.0.0.1", new HashSet<ushort> { 9000 });
        Assert.Contains("9000", procFilter);
    }

    [Fact]
    public void ViewModel_DiagnosticSettingsAndHotkeysRoundTrip()
    {
        var thread = new Thread(() =>
        {
            var (engine, state, controller) = CreateTestHarness();
            string tempDir = Path.Combine(Path.GetTempPath(), "DarkDesyncDiagTest-" + Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            try
            {
                var store = new SettingsStore(Path.Combine(tempDir, "settings.json"));
                using var vm = new MainViewModel(
                    new ProcessService(NullLogger<ProcessService>.Instance),
                    new HotkeyService(NullLogger<HotkeyService>.Instance),
                    controller, engine, store);

                // Configure diagnostic settings
                vm.DiagnosticCycleDurationMs = 175;
                vm.BurstLossPercentage = 45;
                vm.AsymmetricInboundLossPercentage = 30;
                vm.AsymmetricOutboundLossPercentage = 80;
                vm.AuthorizedEndpoint = "10.0.0.5";
                vm.SelectedFreezeHotkey = "F1";
                vm.SelectedBurstHotkey = "F2";
                vm.SelectedAsymmetricHotkey = "F3";

                // Save
                vm.SaveSettingsCommand.Execute(null);

                // Reset
                vm.ResetSettingsCommand.Execute(null);
                Assert.Equal(150, vm.DiagnosticCycleDurationMs);
                Assert.Equal(50, vm.BurstLossPercentage);
                Assert.Equal("127.0.0.1", vm.AuthorizedEndpoint);

                // Load
                vm.LoadSettingsCommand.Execute(null);
                Assert.Equal(175, vm.DiagnosticCycleDurationMs);
                Assert.Equal(45, vm.BurstLossPercentage);
                Assert.Equal(30, vm.AsymmetricInboundLossPercentage);
                Assert.Equal(80, vm.AsymmetricOutboundLossPercentage);
                Assert.Equal("10.0.0.5", vm.AuthorizedEndpoint);
                Assert.Equal("F1", vm.SelectedFreezeHotkey);
                Assert.Equal("F2", vm.SelectedBurstHotkey);
                Assert.Equal("F3", vm.SelectedAsymmetricHotkey);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
    }
}
