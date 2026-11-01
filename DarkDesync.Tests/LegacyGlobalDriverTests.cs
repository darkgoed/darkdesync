using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using DarkDesync.Models;
using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

public sealed class GlobalDriverTheoryAttribute : TheoryAttribute
{
    public GlobalDriverTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DARKDESYNC_DRIVER_TESTS") != "1" ||
            Environment.GetEnvironmentVariable("DARKDESYNC_GLOBAL_TESTS") != "1")
            Skip = "Requires separate explicit GLOBAL test opt-in on an isolated Windows VM; affects all outbound traffic during pulses.";
    }
}

// EXPLICIT GLOBAL CONSENT on an ephemeral Windows CI runner only. While a pulse is
// active the literal outbound filter affects ALL that runner's outbound traffic.
// Assertions generate only controlled local UDP. Never run this suite on a shared host.
[Collection("WinDivert controlled driver")]
public sealed class LegacyGlobalDriverTests(ITestOutputHelper output)
{
    [GlobalDriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(DiagnosticMode.None)]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    public async Task ExplicitGlobal150ms_CapturesUnmappedUdp_StrictRefuses_OutsidePulseUnaffected(DiagnosticMode mode)
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sender.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
        using var engine = DiagnosticModesTests.Engine();
        Assert.False(engine.Start(FilterMode.Outbound, processId: 0)); // Strict cannot guess ownership.
        await sender.SendAsync(new byte[] { 10 }, 1);
        Assert.Equal(new byte[] { 10 }, (await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
        Assert.Equal(0, engine.PacketsIntercepted);
        engine.UpdateFilter(FilterMode.LegacyGlobalOutbound);
        using var controller = new DesyncController(NullLogger<DesyncController>.Instance, engine.StateMachine, engine)
        { ActiveProfile = new IntensityProfile { Name = "compat", DurationMs = 5000, DropPercentage = 100 } };
        Assert.False(controller.TriggerCycle(mode, legacyGlobal: true, globalConsent: false));
        Assert.False(engine.IsRunning); Assert.Equal("false", engine.CurrentFilter);
        await sender.SendAsync(new byte[] { 11 }, 1);
        Assert.Equal(new byte[] { 11 }, (await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
        Assert.Equal(0, engine.PacketsIntercepted);
        var finished = new TaskCompletionSource<CycleRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requested = 0; controller.CountdownTick += (_, total) => requested = total;
        controller.CycleFinished += r => finished.TrySetResult(r);
        Assert.True(controller.TriggerCycle(mode, legacyGlobal: true, globalConsent: true)); // DEFAULT = 150, not profile's 5000.
        Assert.Equal("outbound", engine.CurrentFilter);
        Assert.Contains("GLOBAL", engine.ScopeStatus.Reason);
        Assert.False(engine.IsObservingUdp); Assert.Equal(0, engine.ObservedUdpFlowCount);
        var receive = receiver.ReceiveAsync();
        await sender.SendAsync(new byte[] { 12 }, 1);
        await Until(() => mode == DiagnosticMode.None ? engine.UdpPacketsDropped > 0 : engine.UdpPacketsHeld > 0);
        Assert.False(receive.IsCompleted, "UDP was neither dropped nor held inside the consented pulse");
        var record = await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Until(() => !engine.IsRunning && engine.CurrentFilter == "false");
        Assert.Equal(150, requested);
        Assert.Contains("GLOBAL", record.TargetProcess);
        if (mode == DiagnosticMode.OutboundFreeze)
        {
            Assert.Equal(0, engine.UdpPacketsDropped);
            Assert.Equal(new byte[] { 12 }, (await receive.WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
            receive = receiver.ReceiveAsync();
        }
        else Assert.True(engine.UdpPacketsDropped > 0);
        long before = engine.PacketsIntercepted;
        await sender.SendAsync(new byte[] { 13 }, 1);
        Assert.Equal(new byte[] { 13 }, (await receive.WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
        Assert.Equal(before, engine.PacketsIntercepted); // No global handle, not merely pass-through, outside pulse.
        output.WriteLine($"Strict refused unknown UDP; consented GLOBAL literal outbound {mode}, requested {requested} ms, measured {record.DurationMs} ms; outside pulse not intercepted.");
    }

    [GlobalDriverTheory]
    [Trait("Category", "Driver")]
    [InlineData("cancel")]
    [InlineData("dispose")]
    [InlineData("error")]
    [InlineData("start-log")]
    public async Task GlobalRestoresOnCancelShutdownAndControllerError(string action)
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sender.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
        using var engine = DiagnosticModesTests.Engine();
        engine.UpdateFilter(FilterMode.LegacyGlobalOutbound);
        using var controller = new DesyncController(NullLogger<DesyncController>.Instance, engine.StateMachine, engine);
        if (action == "error") controller.CountdownTick += (_, _) => throw new InvalidOperationException("test controller failure");
        if (action == "start-log") controller.LogMessage += msg => { if (msg.StartsWith("[START]")) throw new InvalidOperationException("test start log failure"); };
        Assert.Equal(action != "start-log", controller.TriggerCycle(DiagnosticMode.OutboundFreeze, legacyGlobal: true, globalConsent: true));
        Task<UdpReceiveResult>? held = null;
        if (action != "error" && action != "start-log")
        {
            held = receiver.ReceiveAsync();
            await sender.SendAsync(new byte[] { 20 }, 1);
            await Until(() => engine.UdpPacketsHeld > 0);
            if (action == "cancel") controller.CancelCycle();
            else engine.Dispose();
        }
        await Until(() => !engine.IsRunning && !engine.IsLegacyGlobalPulse && engine.CurrentFilter == "false");
        if (held != null) Assert.Equal(new byte[] { 20 }, (await held.WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
        long before = engine.PacketsIntercepted;
        await sender.SendAsync(new byte[] { 21 }, 1);
        Assert.Equal(new byte[] { 21 }, (await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
        Assert.Equal(before, engine.PacketsIntercepted);
        output.WriteLine("GLOBAL handle closed and traffic restored after " + action);
    }

    [GlobalDriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(150)]
    public async Task EngineWatchdogClosesGlobalEvenWithoutController(int duration)
    {
        using var engine = DiagnosticModesTests.Engine();
        engine.UpdateFilter(FilterMode.LegacyGlobalOutbound);
        engine.StateMachine.TryActivate();
        Assert.True(engine.BeginLegacyGlobalPulse(duration, consent: true));
        Assert.Equal("outbound", engine.CurrentFilter);
        await Until(() => !engine.IsRunning && !engine.IsLegacyGlobalPulse && engine.CurrentFilter == "false");
        Assert.Equal(DesyncStateMachine.State.Idle, engine.StateMachine.Current);
        output.WriteLine("Independent engine watchdog closed global handle without controller restoration.");
    }

    [GlobalDriverTheory]
    [Trait("Category", "Driver")]
    [InlineData(DiagnosticMode.None)]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    [InlineData(DiagnosticMode.BurstLoss)]
    public async Task GlobalStartStop_RunsBeyond800ms_AndStopReleasesTraffic(DiagnosticMode mode)
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sender.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
        using var engine = DiagnosticModesTests.Engine();
        // Merely selecting GLOBAL never opens WinDivert or changes other applications.
        engine.UpdateFilter(FilterMode.LegacyGlobalOutbound);
        Assert.False(engine.IsRunning);
        Assert.Equal("false", engine.CurrentFilter);
        engine.BurstLossPercentage = 100;
        engine.CycleDropEnabled = true;
        engine.SetDropPercentage(100);
        Assert.True(engine.BeginLegacyGlobalContinuous(mode));
        try
        {
            Assert.True(engine.IsRunning);
            Assert.Equal("outbound", engine.CurrentFilter);
            Assert.Equal(DesyncStateMachine.State.Active, engine.StateMachine.Current);
            // Above the former 800 ms cap: only STOP, close, or a failure should end capture.
            await Task.Delay(1050);
            Assert.True(engine.IsRunning);
            var pending = receiver.ReceiveAsync();
            await sender.SendAsync(new byte[] { 41 }, 1);
            await Until(() => mode == DiagnosticMode.OutboundFreeze ?
                engine.UdpPacketsHeld > 0 : engine.UdpPacketsDropped > 0);
            Assert.False(pending.IsCompleted, "START must apply the selected effect, not just pass traffic through");
            engine.Stop();
            Assert.False(engine.IsRunning);
            Assert.Equal("false", engine.CurrentFilter);
            if (mode == DiagnosticMode.OutboundFreeze)
            {
                Assert.Equal(new byte[] { 41 }, (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
                pending = receiver.ReceiveAsync();
            }
            long captured = engine.PacketsIntercepted;
            await sender.SendAsync(new byte[] { 42 }, 1);
            Assert.Equal(new byte[] { 42 }, (await pending.WaitAsync(TimeSpan.FromSeconds(2))).Buffer);
            Assert.Equal(captured, engine.PacketsIntercepted);
        }
        finally
        {
            engine.Stop();
        }
        output.WriteLine($"GLOBAL {mode}: active after 1050ms, STOP restored normal traffic");
    }

    private static async Task Until(Func<bool> ready)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(1);
        Assert.True(ready(), "Timed out waiting for actual driver/effect/closure evidence");
    }
}
