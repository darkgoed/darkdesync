using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using DarkDesync.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

// Invoke the exact processing seam used by CaptureLoop without opening a driver.
public sealed class PacketEngineSchedulingTests
{
    private static PacketEngine Engine() => new(
        NullLogger<PacketEngine>.Instance,
        new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance),
        new FilterBuilder(NullLogger<FilterBuilder>.Instance),
        new FlowTracker(NullLogger<FlowTracker>.Instance), new DesyncStateMachine());

    private static object? Invoke(PacketEngine engine, string name, params object[] args) =>
        typeof(PacketEngine).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, args);
    private static void Capture(PacketEngine engine, byte id = 1, Random? random = null)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, id);
            Invoke(engine, "ProcessCapturedPacket", buffer, (uint)1, new WinDivertNative.Address(), random ?? new Random(42));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ConcurrentQueue<ThrottledPacket> Queue(PacketEngine engine) =>
        (ConcurrentQueue<ThrottledPacket>)typeof(PacketEngine).GetField("_throttleQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;

    private sealed class EdgeRandom(bool high) : Random
    {
        public override int Next(int minValue, int maxValue) => high ? maxValue - 1 : minValue;
    }

    [Theory]
    [InlineData(false, 0L)]
    [InlineData(true, 110L)]
    public void Jitter_SamplesInclusiveBounds_ClampsDelayAtZero(bool high, long expectedMs)
    {
        using var engine = Engine();
        var jitter = typeof(PacketEngine).GetProperty("ThrottleJitterMs");
        Assert.NotNull(jitter);
        jitter.SetValue(engine, 100);
        engine.ThrottleTimeframeMs = 10;
        engine.IsThrottleActive = true;
        Capture(engine, random: new EdgeRandom(high));
        Assert.True(Queue(engine).TryPeek(out var packet));
        var releaseAt = typeof(ThrottledPacket).GetProperty("ReleaseAtTicks");
        Assert.NotNull(releaseAt);
        Assert.Equal(expectedMs * System.Diagnostics.Stopwatch.Frequency / 1000,
            (long)releaseAt.GetValue(packet)! - packet.TimestampTicks);
    }

    private static void SetField(PacketEngine engine, string name, object value) =>
        typeof(PacketEngine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, value);
    private static bool Release(PacketEngine engine, long now) => (bool)Invoke(engine, "TryReleaseNext", now)!;
    private static long Ticks(long ms) => ms * System.Diagnostics.Stopwatch.Frequency / 1000;
    private static List<byte> FakeSender(PacketEngine engine, Func<long> clock, bool success = true)
    {
        var sent = new List<byte>();
        SetField(engine, "_timestamp", clock);
        SetField(engine, "_sendOverride", (Func<IntPtr, uint, WinDivertNative.Address, bool>)((buffer, _, _) =>
        {
            sent.Add(Marshal.ReadByte(buffer));
            return success;
        }));
        return sent;
    }

    [Fact]
    public void DisabledCycleProfile_PassesPacketsWhileActive()
    {
        using var engine = Engine();
        var property = typeof(PacketEngine).GetProperty("CycleDropEnabled");
        Assert.NotNull(property);
        property.SetValue(engine, false);
        FakeSender(engine, () => 0);
        engine.StateMachine.TryActivate();
        Capture(engine);
        Assert.Equal(0, engine.PacketsDropped);
        Assert.Equal(1, engine.PacketsPassedThrough);
    }

    [Fact]
    public void ContinuousThrottle_HoldsInIdleAndSurvivesCycleCancellation()
    {
        using var engine = Engine();
        var property = typeof(PacketEngine).GetProperty("ThrottleContinuous");
        Assert.NotNull(property);
        property.SetValue(engine, true);
        engine.ThrottleEnabled = true;
        FakeSender(engine, () => 0);
        Capture(engine);
        engine.StateMachine.TryActivate();
        engine.StateMachine.ForceIdle();
        Assert.Single(Queue(engine));
        engine.ThrottleEnabled = false;
        Assert.Empty(Queue(engine));
    }

    [Fact]
    public void ReleaseSpacing_SurvivesAnEmptyQueue()
    {
        using var engine = Engine();
        long now = 0;
        var sent = FakeSender(engine, () => now);
        engine.IsThrottleActive = true;
        engine.ThrottleTimeframeMs = 10;
        engine.ThrottleReleaseDelayMs = 100;
        Capture(engine, 1);
        now = Ticks(10);
        Assert.True(Release(engine, now));
        Capture(engine, 2);
        now = Ticks(20);
        Assert.False(Release(engine, now));
        now = Ticks(110);
        Assert.True(Release(engine, now));
        Assert.Equal(new byte[] { 1, 2 }, sent);
    }

    [Fact]
    public void DisableThrottle_FlushesImmediatelyAndResetsSpacing()
    {
        using var engine = Engine();
        var sent = FakeSender(engine, () => 0);
        engine.ThrottleEnabled = true;
        engine.StateMachine.TryActivate();
        Capture(engine, 1);
        Capture(engine, 2);
        engine.ThrottleEnabled = false;
        Assert.Equal(new byte[] { 1, 2 }, sent);
        Assert.Empty(Queue(engine));
    }

    private sealed class BlockingRandom(ManualResetEventSlim entered, ManualResetEventSlim resume) : Random
    {
        public override int Next(int minValue, int maxValue)
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("enqueue barrier timed out");
            return 0;
        }
    }

    [Fact]
    public async Task Restore_SerializesWithInFlightEnqueue()
    {
        using var engine = Engine();
        var sent = FakeSender(engine, () => 0);
        engine.ThrottleEnabled = true;
        engine.ThrottleJitterMs = 1;
        engine.StateMachine.TryActivate();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var capture = Task.Run(() => Capture(engine, 1, new BlockingRandom(entered, resume)));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        var restore = Task.Run(() => engine.StateMachine.TryBeginRestore());
        try { Assert.NotSame(restore, await Task.WhenAny(restore, Task.Delay(150))); }
        finally { resume.Set(); await capture.WaitAsync(TimeSpan.FromSeconds(3)); await restore.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.Empty(Queue(engine));
        Assert.Equal(new byte[] { 1 }, sent);
    }

    [Fact]
    public void Stop_LateReceivedPacketIsNotOrphanedInQueue()
    {
        using var engine = Engine();
        var sent = FakeSender(engine, () => 0, success: false);
        engine.IsThrottleActive = true;
        SetField(engine, "_isRunning", true);
        engine.Stop(); // No native handle was ever opened.
        Capture(engine, 1); // A successful Recv can finish as Stop closes the handle.
        Assert.Empty(Queue(engine));
        Assert.Equal(1, engine.PacketsDropped);
        Assert.Equal(0, engine.PacketsPassedThrough);
        Assert.Single(sent);
    }

    [Fact]
    public void Stop_LatePacketCannotOvertakeHeldPackets()
    {
        using var engine = Engine();
        var sent = FakeSender(engine, () => 0);
        engine.IsThrottleActive = true;
        Capture(engine, 1);
        using var cts = new CancellationTokenSource();
        SetField(engine, "_cts", cts);
        SetField(engine, "_isRunning", true);
        using var registration = cts.Token.Register(() => Capture(engine, 2));
        engine.Stop();
        Assert.Equal(new byte[] { 1, 2 }, sent);
        Assert.Empty(Queue(engine));
    }

    [Fact]
    public void FailedPassThrough_DoesNotCountAsDelivered()
    {
        using var engine = Engine();
        Capture(engine);
        Assert.Equal(1, engine.PacketsIntercepted);
        Assert.Equal(0, engine.PacketsPassedThrough);
        Assert.Equal(1, engine.PacketsDropped);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(100, true)]
    public void ThrottleChance_Extremes_SelectOnlyThrottleOrPass(int chance, bool queues)
    {
        using var engine = Engine();
        var sent = FakeSender(engine, () => 0);
        engine.ThrottleEnabled = true;
        engine.ThrottleChance = chance;
        engine.StateMachine.TryActivate();
        Capture(engine);
        Assert.Equal(queues ? 1 : 0, engine.PacketsThrottled);
        Assert.Equal(queues ? 0 : 1, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsDropped);
        Assert.Equal(queues ? 0 : 1, sent.Count);
    }

    [Fact]
    public void ExplicitDrop_TakesPriorityOverThrottle()
    {
        using var engine = Engine();
        var sent = FakeSender(engine, () => 0);
        engine.ThrottleEnabled = true;
        engine.IsDropActive = true;
        engine.StateMachine.TryActivate();
        Capture(engine);
        Assert.Equal(1, engine.PacketsDropped);
        Assert.Equal(0, engine.PacketsThrottled);
        Assert.Empty(sent);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(100, 1, 0)]
    public void ImplicitDrop_WithoutThrottle_RespectsPercentage(int percentage, long dropped, long passed)
    {
        using var engine = Engine();
        FakeSender(engine, () => 0);
        engine.SetDropPercentage(percentage);
        engine.StateMachine.TryActivate();
        Capture(engine);
        Assert.Equal(dropped, engine.PacketsDropped);
        Assert.Equal(passed, engine.PacketsPassedThrough);
    }

    private sealed class RollRandom(int roll) : Random
    {
        public override int Next(int maxValue) => roll;
    }

    [Theory]
    [InlineData(49, 1, 0)]
    [InlineData(50, 0, 1)]
    public void ThrottleChance_UsesStrictPercentageBoundary(int roll, long queued, long passed)
    {
        using var engine = Engine();
        FakeSender(engine, () => 0);
        engine.IsThrottleActive = true;
        engine.ThrottleChance = 50;
        Capture(engine, random: new RollRandom(roll));
        Assert.Equal(queued, engine.PacketsThrottled);
        Assert.Equal(passed, engine.PacketsPassedThrough);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2001, 2000)]
    [InlineData(123, 123)]
    public void JitterSetting_IsClamped(int value, int expected)
    {
        using var engine = Engine();
        engine.ThrottleJitterMs = value;
        Assert.Equal(expected, engine.ThrottleJitterMs);
    }

    [Fact]
    public void Release_UsesFrozenJitterDeadlineAndPreservesFifoInBurst()
    {
        using var engine = Engine();
        long now = 0;
        var sent = FakeSender(engine, () => now);
        engine.IsThrottleActive = true;
        engine.ThrottleTimeframeMs = 100;
        engine.ThrottleJitterMs = 100;
        Capture(engine, 1, new EdgeRandom(true)); // deadline 200ms
        Capture(engine, 2, new EdgeRandom(false)); // deadline 0ms, must not overtake
        engine.ThrottleTimeframeMs = 10;
        engine.ThrottleJitterMs = 0;
        Assert.False(Release(engine, 0));
        Assert.False(Release(engine, Ticks(199)));
        now = Ticks(200);
        Assert.True(Release(engine, now));
        Assert.True(Release(engine, now));
        Assert.False(Release(engine, now));
        Assert.Equal(new byte[] { 1, 2 }, sent);
        Assert.Equal(2, engine.PacketsPassedThrough);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_FlushesOrderedPacketsOnceAndIgnoresSpacing(bool forceIdle)
    {
        using var engine = Engine();
        var sent = FakeSender(engine, () => 0);
        engine.ThrottleEnabled = true;
        engine.ThrottleReleaseDelayMs = 200;
        engine.StateMachine.TryActivate();
        Capture(engine, 1);
        Capture(engine, 2);
        if (forceIdle) engine.StateMachine.ForceIdle();
        else engine.StateMachine.TryBeginRestore();
        engine.FlushThrottleQueue();
        Assert.Equal(new byte[] { 1, 2 }, sent);
        Assert.Empty(Queue(engine));
        Capture(engine, 3);
        Assert.Equal(new byte[] { 1, 2, 3 }, sent);
    }

    [Fact]
    public void FailedThrottleRelease_IsCountedAsDropNotDelivery()
    {
        using var engine = Engine();
        FakeSender(engine, () => 0, success: false);
        engine.IsThrottleActive = true;
        Capture(engine);
        engine.FlushThrottleQueue();
        Assert.Equal(1, engine.PacketsThrottled);
        Assert.Equal(1, engine.PacketsDropped);
        Assert.Equal(0, engine.PacketsPassedThrough);
        Assert.Empty(Queue(engine));
    }

    [Fact]
    public async Task ConcurrentReleaseAndFlush_PreserveFifoWithoutDuplicates()
    {
        using var engine = Engine();
        var sent = FakeSender(engine, () => Ticks(1000));
        engine.IsThrottleActive = true;
        engine.ThrottleTimeframeMs = 10;
        for (byte i = 0; i < 100; i++) Capture(engine, i);
        await Task.WhenAll(Task.Run(() => { while (Release(engine, Ticks(2000))) { } }),
            Task.Run(engine.FlushThrottleQueue), Task.Run(engine.FlushThrottleQueue));
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (byte)i), sent);
        Assert.Equal(100, engine.PacketsPassedThrough);
        Assert.Empty(Queue(engine));
    }

    [Fact]
    public void Active_DefaultDrop100_ThrottleEnabled_QueuesInsteadOfDropping()
    {
        using var engine = Engine();
        engine.ThrottleEnabled = true;
        Assert.True(engine.StateMachine.TryActivate());
        Capture(engine);
        Assert.Equal(0, engine.PacketsDropped);
        Assert.Equal(1, engine.PacketsThrottled);
    }
}
