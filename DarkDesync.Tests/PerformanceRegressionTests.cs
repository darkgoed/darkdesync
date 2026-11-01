using System.Diagnostics;
using System.Reflection;
using DarkDesync.Core;
using DarkDesync.Services;
using DarkDesync.ViewModels;
using DarkDesync.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

public sealed class PerformanceRegressionTests
{
    private readonly ITestOutputHelper _output;
    public PerformanceRegressionTests(ITestOutputHelper output) => _output = output;

    private static PacketEngine CreateEngine() => new(
        NullLogger<PacketEngine>.Instance,
        new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance),
        new FilterBuilder(NullLogger<FilterBuilder>.Instance),
        new FlowTracker(NullLogger<FlowTracker>.Instance),
        new DesyncStateMachine());

    [Fact]
    public void PacketStats_BurstDoesNotFloodSubscribers()
    {
        using var engine = CreateEngine();
        var method = typeof(PacketEngine).GetMethod("NotifyStats", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var notify = (Action)method.CreateDelegate(typeof(Action), engine);
        int notifications = 0;
        engine.StatsUpdated += (_, _, _, _) => notifications++;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 100_000; i++) notify();
        watch.Stop();
        int upperBound = 1 + (int)Math.Ceiling(watch.Elapsed.TotalMilliseconds / 250);
        _output.WriteLine($"100000 telemetry requests: {notifications} notifications in {watch.Elapsed.TotalMilliseconds:F2} ms; limit {upperBound}");
        Assert.InRange(notifications, 1, upperBound);
    }

    [Fact]
    public void InterfaceTelemetry_IsLowFrequencyAndLowPriority()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var controller = new DesyncController(NullLogger<DesyncController>.Instance, new DesyncStateMachine());
                using var vm = new MainViewModel(
                    new ProcessService(NullLogger<ProcessService>.Instance),
                    new HotkeyService(NullLogger<HotkeyService>.Instance), controller);
                var timer = typeof(MainViewModel).GetField("_statsTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                Assert.Equal(TimeSpan.FromMilliseconds(250), timer.GetType().GetProperty("Interval")!.GetValue(timer));
                var priority = timer.GetType().GetField("_priority", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(timer);
                Assert.Equal("Background", priority!.ToString());
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "UI telemetry test did not finish");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task Countdown_LimitsUiTicksWithoutShorteningCycle()
    {
        using var controller = new DesyncController(NullLogger<DesyncController>.Instance, new DesyncStateMachine());
        controller.ActiveProfile = new IntensityProfile { Name = "Performance", DurationMs = 330, DropPercentage = 0 };
        int ticks = 0;
        int lastRemaining = -1;
        var finished = new TaskCompletionSource<CycleRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.CountdownTick += (remaining, _) => { ticks++; lastRemaining = remaining; };
        controller.CycleFinished += record => finished.TrySetResult(record);
        Assert.True(controller.TriggerCycle());
        var record = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _output.WriteLine($"330 ms cycle: {ticks} countdown updates, actual duration {record.DurationMs} ms");
        Assert.InRange(ticks, 2, 9);
        Assert.Equal(0, lastRemaining);
        Assert.True(record.CompletedNormally);
        Assert.True(record.DurationMs >= 330);
    }
}
