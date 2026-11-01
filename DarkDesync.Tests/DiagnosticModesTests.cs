using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using DarkDesync.Core;
using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public sealed class DiagnosticModesTests
{
    internal static PacketEngine Engine() => new(NullLogger<PacketEngine>.Instance,
        new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance),
        new FilterBuilder(NullLogger<FilterBuilder>.Instance),
        new FlowTracker(NullLogger<FlowTracker>.Instance), new DesyncStateMachine());

    internal static ProcessNetworkScanner.ProcessEndpoint Endpoint(ushort remotePort = 443) =>
        new(6, 40000, remotePort, BitConverter.ToUInt32(IPAddress.Parse("127.0.0.1").GetAddressBytes()),
            BitConverter.ToUInt32(IPAddress.Parse("127.0.0.2").GetAddressBytes()));

    internal static byte[] Packet(bool outbound, ushort remotePort = 443, byte protocol = 6)
    {
        var bytes = new byte[40];
        bytes[0] = 0x45; bytes[3] = 40; bytes[9] = protocol;
        var local = IPAddress.Parse("127.0.0.1").GetAddressBytes();
        var remote = IPAddress.Parse("127.0.0.2").GetAddressBytes();
        (outbound ? local : remote).CopyTo(bytes, 12);
        (outbound ? remote : local).CopyTo(bytes, 16);
        ushort src = outbound ? (ushort)40000 : remotePort;
        ushort dst = outbound ? remotePort : (ushort)40000;
        bytes[20] = (byte)(src >> 8); bytes[21] = (byte)src;
        bytes[22] = (byte)(dst >> 8); bytes[23] = (byte)dst;
        bytes[32] = 0x50;
        if (protocol == 17) bytes[25] = 20; // UDP header + payload length.
        return bytes;
    }

    internal static bool Capture(PacketEngine engine, bool outbound = true, ushort remotePort = 443,
        byte protocol = 6, bool fragment = false, bool loopback = true)
    {
        var bytes = Packet(outbound, remotePort, protocol);
        if (fragment) bytes[6] = 0x20;
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            var addr = new WinDivertNative.Address { Flags = (outbound ? 1u << 17 : 0) | (loopback ? 1u << 18 : 0) };
            return (bool)typeof(PacketEngine).GetMethod("ProcessCapturedPacket", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(engine, [ptr, (uint)bytes.Length, addr, new Random(42)])!;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    internal static void Authorize(PacketEngine engine)
    {
        engine.TargetAliveOverride = () => true;
        engine.EndpointsOverride = _ => [Endpoint()];
        engine.SendOverride = (_, _, _) => true;
    }

    [Theory]
    [InlineData(DiagnosticMode.BurstLoss, true, 100, 1, 0)]
    [InlineData(DiagnosticMode.BurstLoss, false, 0, 0, 1)]
    [InlineData(DiagnosticMode.AsymmetricLoss, true, 100, 1, 0)]
    [InlineData(DiagnosticMode.AsymmetricLoss, false, 0, 0, 1)]
    public void LossModes_OverrideLegacyAndContinuousThrottle(DiagnosticMode mode, bool outbound, int loss, long dropped, long passed)
    {
        using var engine = Engine(); Authorize(engine);
        engine.ActiveDiagnosticMode = mode;
        engine.BurstLossPercentage = loss;
        engine.AsymmetricOutboundLossPercentage = 100;
        engine.AsymmetricInboundLossPercentage = 0;
        engine.ThrottleEnabled = true; engine.ThrottleContinuous = true;
        engine.StateMachine.TryActivate();
        Capture(engine, outbound);
        Assert.Equal(dropped, engine.PacketsDropped);
        Assert.Equal(passed, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsThrottled);
    }

    [Fact]
    public void Freeze_HoldsOnlyOutboundUntilRestore_NotThrottleDeadline()
    {
        using var engine = Engine(); Authorize(engine);
        engine.ActiveDiagnosticMode = DiagnosticMode.OutboundFreeze;
        engine.StateMachine.TryActivate();
        Capture(engine); Capture(engine, false);
        Assert.Equal(1, engine.PacketsThrottled);
        Assert.Equal(1, engine.PacketsPassedThrough);
        var release = typeof(PacketEngine).GetMethod("TryReleaseNext", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.False((bool)release.Invoke(engine, [long.MaxValue / 2])!);
        engine.StateMachine.TryBeginRestore();
        engine.ActiveDiagnosticMode = DiagnosticMode.None;
        engine.FlushThrottleQueue(); engine.FlushThrottleQueue();
        Assert.Equal(2, engine.PacketsPassedThrough); Assert.Equal(0, engine.PacketsDropped);
    }

    [Theory]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    [InlineData(DiagnosticMode.BurstLoss)]
    [InlineData(DiagnosticMode.AsymmetricLoss)]
    public void AllModes_RejectOtherTupleUdpFragmentsDeadTargetAndUnknownOwnership(DiagnosticMode mode)
    {
        using var engine = Engine(); Authorize(engine);
        engine.ActiveDiagnosticMode = mode;
        engine.BurstLossPercentage = engine.AsymmetricOutboundLossPercentage = 100;
        engine.StateMachine.TryActivate();
        Capture(engine, remotePort: 444); // Same local port and loopback is not authorization.
        Capture(engine, protocol: 17);
        Capture(engine, fragment: true);
        engine.TargetAliveOverride = () => false; Capture(engine);
        engine.TargetAliveOverride = () => true;
        engine.EndpointsOverride = _ => []; Capture(engine);
        engine.EndpointsOverride = _ => throw new InvalidOperationException("unavailable"); Capture(engine);
        Assert.Equal(6, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsThrottled); Assert.Equal(0, engine.PacketsDropped);
    }

    [Fact]
    public void MissingTarget_BlocksEveryFilterBeforeOpeningDriver()
    {
        using var engine = Engine(); string? error = null;
        engine.ErrorOccurred += e => error = e;
        foreach (var mode in Enum.GetValues<FilterMode>())
        {
            Assert.False(engine.Start(mode, customFilter: "true"));
            Assert.Equal("false", engine.CurrentFilter); Assert.False(engine.IsRunning);
        }
        Assert.Contains("bloqueada", error);
    }

    [Theory]
    [InlineData(DiagnosticMode.OutboundFreeze)]
    [InlineData(DiagnosticMode.BurstLoss)]
    [InlineData(DiagnosticMode.AsymmetricLoss)]
    public async Task UnifiedCycle_RestoresModeAndRejectsOverlap(DiagnosticMode mode)
    {
        using var engine = Engine(); Authorize(engine);
        using var controller = new DesyncController(NullLogger<DesyncController>.Instance, engine.StateMachine, engine);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.CycleFinished += _ => done.TrySetResult();
        Assert.True(controller.TriggerCycle(mode, 150));
        Assert.False(controller.TriggerCycle(mode, 150));
        Capture(engine);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(DesyncStateMachine.State.Idle, controller.CurrentState);
        Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
        if (mode == DiagnosticMode.OutboundFreeze) Assert.Equal(1, engine.PacketsPassedThrough);
    }

    [Fact]
    public void ZeroDuration_IsNoOp_AndCancellationCannotRestoreNextPulse()
    {
        using var engine = Engine(); Authorize(engine);
        using var controller = new DesyncController(NullLogger<DesyncController>.Instance, engine.StateMachine, engine);
        Assert.False(controller.TriggerCycle(DiagnosticMode.OutboundFreeze, 0));
        Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
        for (int i = 0; i < 20; i++)
        {
            Assert.True(controller.TriggerCycle(DiagnosticMode.OutboundFreeze, 800));
            Capture(engine);
            controller.CancelCycle();
            Assert.Equal(DesyncStateMachine.State.Idle, controller.CurrentState);
            Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
        }
        Assert.Equal(20, engine.PacketsPassedThrough);
    }

    [Theory]
    [InlineData(-1, 0)] [InlineData(0, 0)] [InlineData(150, 150)] [InlineData(800, 800)] [InlineData(801, 800)]
    public void Settings_ClampDuration(int input, int expected) =>
        Assert.Equal(expected, new AppSettings { DiagnosticDurationMs = input }.Validate().DiagnosticDurationMs);

    [Fact]
    public void HeldGlobalKey_DoesNotRepeat_UntilRelease()
    {
        using var hotkey = new HotkeyService(NullLogger<HotkeyService>.Instance);
        Assert.True(hotkey.AcceptKeyTransition(0x20, true));
        for (int i = 0; i < 100; i++) Assert.False(hotkey.AcceptKeyTransition(0x20, true));
        Assert.False(hotkey.AcceptKeyTransition(0x20, false));
        Assert.True(hotkey.AcceptKeyTransition(0x20, true));
        Assert.False(hotkey.AcceptKeyTransition(0x75, true)); // F6 is not a separate mode hotkey.
    }

    [Fact]
    public void DisposeTwiceAndCancelAfterDispose_RestoreWithoutDisposedToken()
    {
        using var engine = Engine(); Authorize(engine);
        var controller = new DesyncController(NullLogger<DesyncController>.Instance, engine.StateMachine, engine);
        Assert.True(controller.TriggerCycle(DiagnosticMode.OutboundFreeze, 800));
        Capture(engine);
        controller.Dispose(); controller.Dispose(); controller.CancelCycle();
        Assert.False(controller.TriggerCycle(DiagnosticMode.OutboundFreeze, 150));
        Assert.Equal(DesyncStateMachine.State.Idle, controller.CurrentState);
        Assert.Equal(1, engine.PacketsPassedThrough);
    }

    [Fact]
    public async Task LostTargetWhileCapturing_StopsAndReportsWithoutDroppingPacket()
    {
        using var engine = Engine(); Authorize(engine);
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ErrorOccurred += reason => stopped.TrySetResult(reason);
        engine.ActiveDiagnosticMode = DiagnosticMode.BurstLoss;
        engine.BurstLossPercentage = 100; engine.StateMachine.TryActivate();
        typeof(PacketEngine).GetField("_isRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, true);
        engine.TargetAliveOverride = () => false;
        Capture(engine);
        Assert.Contains("processo-alvo", await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(engine.IsRunning);
        Assert.Equal(1, engine.PacketsPassedThrough);
        Assert.Equal(0, engine.PacketsDropped);
        Assert.Equal(DesyncStateMachine.State.Idle, engine.StateMachine.Current);
    }
}
