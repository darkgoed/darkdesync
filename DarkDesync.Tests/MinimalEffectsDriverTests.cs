using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

[Collection("WinDivert controlled driver")]
public sealed class MinimalEffectsDriverTests(ITestOutputHelper output)
{
    [GlobalDriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    [InlineData(DiagnosticMode.MicroFreeze)]
    [InlineData(DiagnosticMode.SmoothDelay)]
    [InlineData(DiagnosticMode.SoftJitter)]
    public async Task OwnLoopback_200msSpacePulse_DeliversFifo_ThenArmed_StopDrains(DiagnosticMode mode)
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sender.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
        using var engine = DiagnosticModesTests.Engine();
        using var hotkey = new HotkeyService(NullLogger<HotkeyService>.Instance);
        int accepted = 0;
        hotkey.HotkeyPressed += () => { if (engine.TriggerArmedEffect(mode, 200)) accepted++; };
        hotkey.DispatchKeyTransition(0x20, true); hotkey.DispatchKeyTransition(0x20, false);
        Assert.Equal(0, accepted); Assert.False(engine.IsCaptureRequested);
        engine.UpdateFilter(FilterMode.LegacyGlobalOutbound);
        Assert.True(engine.BeginLegacyGlobalContinuous(DiagnosticMode.None));
        try
        {
            Assert.Equal("outbound", engine.CurrentFilter);
            await Send(0); Assert.Equal(0, await Receive());
            Assert.Equal(0, engine.PacketsThrottled); Assert.Equal(0, engine.PacketsDropped);
            var workers = engine.WorkerSnapshot;
            for (int cycle = 0; cycle < 2; cycle++)
            {
                long held = engine.UdpPacketsHeld;
                hotkey.DispatchKeyTransition(0x20, true);
                for (int repeat = 0; repeat < 100; repeat++) hotkey.DispatchKeyTransition(0x20, true);
                Assert.Equal(cycle + 1, accepted);
                for (byte id = 1; id <= 16; id++) await Send(id);
                for (byte id = 1; id <= 16; id++) Assert.Equal(id, await Receive());
                await Until(() => engine.StateMachine.Current == DesyncStateMachine.State.Armed);
                Assert.True(engine.UdpPacketsHeld > held);
                Assert.Equal(0, engine.PacketsDropped);
                Assert.True(engine.IsRunning); Assert.Same(workers.Capture, engine.WorkerSnapshot.Capture);
                Assert.Same(workers.Release, engine.WorkerSnapshot.Release);
                long afterHeld = engine.PacketsThrottled;
                await Send(50); Assert.Equal(50, await Receive());
                Assert.Equal(afterHeld, engine.PacketsThrottled);
                hotkey.DispatchKeyTransition(0x20, false);
            }
            hotkey.DispatchKeyTransition(0x20, true); hotkey.DispatchKeyTransition(0x20, false);
            long before = engine.UdpPacketsHeld;
            await Send(99);
            await Until(() => engine.UdpPacketsHeld > before);
            engine.Stop();
            Assert.Equal(99, await Receive());
            Assert.False(engine.IsRunning); Assert.False(engine.IsCaptureRequested);
            Assert.Null(engine.WorkerSnapshot.Capture); Assert.Null(engine.WorkerSnapshot.Release);
            Assert.Equal("false", engine.CurrentFilter); Assert.Equal(0, engine.PacketsDropped);
            output.WriteLine($"{mode}: own UDP loopback, START pass-through, two 200ms simulated Space pulses, 16/16 FIFO each, unchanged workers, STOP drained; no driver-reported loss. {engine.ProtocolCounters}");
        }
        finally { engine.Stop(); }

        async Task Send(byte id) { await sender.SendAsync(new[] { id }, 1); }
        async Task<byte> Receive() => Assert.Single((await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
    }

    private static async Task Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(1);
        Assert.True(ready(), "No real driver evidence before timeout");
    }
}
