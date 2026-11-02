using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

[Collection("WinDivert controlled driver")]
public sealed class GlobalPacketSizeDriverTests(ITestOutputHelper output)
{
    [GlobalDriverTheory, InlineData(200), Trait("Category", "Driver")]
    public async Task RealVm_GlobalSize_NoPid_Consent_RealSizePacingFifo_ExpiryAndStop(int duration)
    {
        using var rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var tx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        tx.Connect((IPEndPoint)rx.Client.LocalEndPoint!);
        ConfigurationBehaviorTests.WithVm((vm, e, _, _) =>
        {
            vm.SelectedFilterModeText = vm.FilterModeOptions[4];
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.PacketSizeAwareShaping);
            Assert.Null(vm.SelectedProcess);
            vm.ToggleEngineCommand.Execute(null);
            Assert.False(e.IsCaptureRequested); Assert.Contains("Consentimento GLOBAL", vm.StartStatusText);
            vm.GlobalPacketSizeConsent = true;
            vm.ToggleEngineCommand.Execute(null);
            Assert.True(e.IsRunning); Assert.Equal("outbound", e.CurrentFilter);
            Assert.Equal(vm.FilterModeOptions[4], vm.SelectedFilterModeText);
            Assert.False(e.IsEffectActive);
            Send(99, 100); Assert.Equal(99, Receive().Buffer[0]); Assert.Equal(0, e.PacketsThrottled);
            double small = Pulse(100), large = Pulse(1000);
            Assert.True(large >= 70, $"Large-packet pacing not observed: {large:F1}ms");
            Assert.True(large > small + 40, $"No size-class distinction: small={small:F1}, large={large:F1}");
            long held = e.PacketsThrottled;
            vm.TriggerCycleCommand.Execute(null); Assert.True(e.IsEffectActive);
            Send(10, 1000); Send(11, 1000); Send(12, 1000);
            UdpDriverIntegrationTests.WaitUntil(() => e.PacketsThrottled >= held + 3).GetAwaiter().GetResult();
            vm.ToggleEngineCommand.Execute(null);
            Assert.False(e.IsRunning); Assert.False(e.IsCaptureRequested);
            Assert.Equal(new byte[] { 10, 11, 12 }, Enumerable.Range(0, 3).Select(_ => Receive().Buffer[0]));
            Thread.Sleep(40); Assert.Equal(0, rx.Available); Assert.Equal(0, e.PacketsDropped);
            Assert.Null(e.WorkerSnapshot.Capture); Assert.Null(e.WorkerSnapshot.Release);
            vm.GlobalPacketSizeConsent = false;
            vm.ToggleEngineCommand.Execute(null); Assert.False(e.IsCaptureRequested);
            output.WriteLine($"Native GLOBAL size pacing: small second={small:F1}ms, large second={large:F1}ms; literal outbound; no PID; START pass-through; 200ms expiry; FIFO, no replay; STOP drained/closed. Only own loopback traffic generated.");

            double Pulse(int size)
            {
                vm.DiagnosticCycleDurationMs = duration;
                vm.TriggerCycleCommand.Execute(null); Assert.True(e.IsEffectActive);
                var clock = Stopwatch.StartNew();
                for (byte id = 0; id < 3; id++) Send(id, size);
                double second = 0;
                for (byte id = 0; id < 3; id++)
                {
                    Assert.Equal(id, Receive().Buffer[0]);
                    if (id == 1) second = clock.Elapsed.TotalMilliseconds;
                }
                UdpDriverIntegrationTests.WaitUntil(() => !e.IsEffectActive).GetAwaiter().GetResult();
                Assert.True(e.IsRunning); Assert.Equal(DesyncStateMachine.State.Armed, e.StateMachine.Current);
                return second;
            }
        });
        await Task.CompletedTask;
        void Send(byte id, int size) { var bytes = new byte[size]; bytes[0] = id; tx.Send(bytes, bytes.Length); }
        UdpReceiveResult Receive() => rx.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
    }
}
