using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

// Only an explicitly opted-in isolated Windows VM. Never real game/anti-cheat traffic.
[Collection("WinDivert controlled driver")]
public sealed class ArmedGlobalDriverTests(ITestOutputHelper output)
{
    [GlobalDriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    [InlineData(DiagnosticMode.AsymmetricLoss)]
    [InlineData(DiagnosticMode.LatencyDelay)]
    [InlineData(DiagnosticMode.BurstLoss)]
    [InlineData(DiagnosticMode.PacketDrop)]
    [InlineData(DiagnosticMode.None)]
    public async Task StartArms_SimulatedSpaceTwoPulses_RestoresWithoutClosing_StopCloses(DiagnosticMode selected)
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sender.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
        using var engine = DiagnosticModesTests.Engine();
        using var hotkey = new HotkeyService(NullLogger<HotkeyService>.Instance);
        int accepted = 0;
        hotkey.HotkeyPressed += () => { if (engine.TriggerArmedEffect(selected, 400)) accepted++; };
        hotkey.DispatchKeyTransition(0x20, true); hotkey.DispatchKeyTransition(0x20, false);
        Assert.False(engine.IsCaptureRequested); Assert.False(engine.IsRunning);
        engine.UpdateFilter(FilterMode.LegacyGlobalOutbound);
        engine.SetDropPercentage(100);
        engine.BurstLossPercentage = engine.AsymmetricOutboundLossPercentage = 100;
        engine.AsymmetricInboundLossPercentage = 100; // Still no inbound capture in literal outbound.
        engine.CycleDropEnabled = engine.ThrottleEnabled = engine.ThrottleContinuous = true;
        engine.GlobalDelayMs = 3000; // Longer than pulse: timer must drain FIFO, not close capture.
        Assert.True(engine.BeginLegacyGlobalContinuous(selected));
        Assert.Equal("outbound", engine.CurrentFilter);
        Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
        try
        {
            for (byte id = 1; id <= 8; id++) await SendAndReceive(id);
            Assert.True(engine.PacketsIntercepted >= 8);
            Assert.Equal(0, engine.PacketsDropped); Assert.Equal(0, engine.PacketsThrottled);
            for (int cycle = 0; cycle < 2; cycle++)
            {
                long dropped = engine.UdpPacketsDropped, held = engine.UdpPacketsHeld;
                var workers = engine.WorkerSnapshot;
                hotkey.DispatchKeyTransition(0x20, true);
                for (int repeat = 0; repeat < 50; repeat++) hotkey.DispatchKeyTransition(0x20, true);
                Assert.Equal(selected == DiagnosticMode.None ? 0 : cycle + 1, accepted);
                if (selected == DiagnosticMode.None)
                {
                    await SendAndReceive((byte)(20 + cycle));
                    Assert.Equal(dropped, engine.UdpPacketsDropped); Assert.Equal(held, engine.UdpPacketsHeld);
                }
                else
                {
                    var pending = receiver.ReceiveAsync();
                    byte id = (byte)(30 + cycle);
                    await sender.SendAsync(new[] { id }, 1);
                    bool retaining = selected is DiagnosticMode.OutboundFreeze or DiagnosticMode.LatencyDelay;
                    await Until(() => retaining ? engine.UdpPacketsHeld > held : engine.UdpPacketsDropped > dropped);
                    Assert.False(pending.IsCompleted, "No hold/drop within actual pulse window");
                    Assert.False(engine.TriggerArmedEffect(selected, 400));
                    await Until(() => engine.StateMachine.Current == DesyncStateMachine.State.Armed);
                    Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
                    Assert.True(engine.IsRunning); Assert.Equal("outbound", engine.CurrentFilter);
                    Assert.Same(workers.Capture, engine.WorkerSnapshot.Capture);
                    Assert.Same(workers.Release, engine.WorkerSnapshot.Release);
                    if (retaining)
                    {
                        Assert.Equal(new[] { id }, (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
                        pending = receiver.ReceiveAsync();
                    }
                    await sender.SendAsync(new byte[] { 60 }, 1);
                    Assert.Equal(new byte[] { 60 }, (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
                    long afterDropped = engine.UdpPacketsDropped, afterHeld = engine.UdpPacketsHeld;
                    await SendAndReceive(61);
                    Assert.Equal(afterDropped, engine.UdpPacketsDropped); Assert.Equal(afterHeld, engine.UdpPacketsHeld);
                }
                hotkey.DispatchKeyTransition(0x20, false);
            }
            engine.Stop();
            Assert.False(engine.IsRunning); Assert.False(engine.IsCaptureRequested);
            Assert.Null(engine.WorkerSnapshot.Capture); Assert.Null(engine.WorkerSnapshot.Release);
            Assert.Equal("false", engine.CurrentFilter);
            long intercepted = engine.PacketsIntercepted;
            await SendAndReceive(99);
            Assert.Equal(intercepted, engine.PacketsIntercepted);
            output.WriteLine($"{selected}: START 8 delivered / dropped=held=0; {accepted} simulated Space pulses; same capture workers; timed return to pass-through; STOP closed. Counters: {engine.ProtocolCounters}");
        }
        finally { engine.Stop(); }

        async Task SendAndReceive(byte id)
        {
            await sender.SendAsync(new[] { id }, 1);
            Assert.Equal(new[] { id }, (await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
        }
    }

    private static async Task Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(2);
        Assert.True(ready(), "Timed out waiting for real driver evidence");
    }
}
