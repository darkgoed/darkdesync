using System.IO;
using System.Reflection;
using DarkDesync.Core;
using DarkDesync.Services;
using DarkDesync.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;
public sealed class ConfigurationBehaviorTests
{
    private static void WithVm(Action<MainViewModel, PacketEngine, DesyncController, string> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "DarkDesyncConfig-" + Guid.NewGuid());
            try
            {
                var state = new DesyncStateMachine();
                using var engine = new PacketEngine(NullLogger<PacketEngine>.Instance,
                    new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance), new FilterBuilder(NullLogger<FilterBuilder>.Instance),
                    new FlowTracker(NullLogger<FlowTracker>.Instance), state);
                using var controller = new DesyncController(NullLogger<DesyncController>.Instance, state, engine);
                using var vm = new MainViewModel(new ProcessService(NullLogger<ProcessService>.Instance),
                    new HotkeyService(NullLogger<HotkeyService>.Instance), controller, engine, new SettingsStore(Path.Combine(dir, "settings.json")));
                action(vm, engine, controller, dir);
            }
            catch (Exception ex) { failure = ex; }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [Fact]
    public void PresetsRemainFixedAfterCustomEditsAndLoading() => WithVm((vm, engine, controller, dir) =>
    {
        vm.CustomDurationMs = 1234; vm.DropPercentage = 37;
        vm.SelectProfileCommand.Execute(vm.Profiles[1]);
        vm.SelectProfileCommand.Execute(vm.Profiles[0]);
        Assert.Equal(800, vm.CustomDurationMs); Assert.Equal(100, vm.DropPercentage);
    });
    [Fact]
    public void DisableThrottleDuringCycleCancelsInsteadOfFallingBackToDrop() => WithVm((vm, engine, controller, dir) =>
    {
        vm.ThrottleEnabled = true;
        Assert.True(controller.TriggerCycle());
        vm.ThrottleEnabled = false;
        Assert.Equal(DesyncStateMachine.State.Idle, controller.CurrentState);
    });
    [Fact]
    public void DisabledEffectsDoNotStartCaptureOrActivateState() => WithVm((vm, engine, controller, dir) =>
    {
        vm.CycleProfileEnabled = false; vm.TriggerCycleCommand.Execute(null);
        Assert.False(engine.IsRunning); Assert.Equal(DesyncStateMachine.State.Idle, controller.CurrentState);
        vm.ThrottleEnabled = true; vm.SelectedThrottleMode = vm.ThrottleModeOptions[1];
        vm.TriggerCycleCommand.Execute(null);
        Assert.False(engine.IsRunning); Assert.Equal(DesyncStateMachine.State.Idle, controller.CurrentState);
    });
    [Fact]
    public void JitterOnlyCycleRequiresStart_AndProfileToggleReturnsToArmed() => WithVm((vm, engine, controller, dir) =>
    {
        vm.CycleProfileEnabled = false; vm.ThrottleEnabled = true;
        try
        {
            vm.TriggerCycleCommand.Execute(null);
            Assert.False(engine.IsCaptureRequested);
            Assert.Equal(DesyncStateMachine.State.Idle, controller.CurrentState);
            ArmedCaptureTests.ModelArm(engine, global: false);
            vm.TriggerCycleCommand.Execute(null);
            Assert.Equal(DesyncStateMachine.State.Active, controller.CurrentState); Assert.False(engine.CycleDropEnabled);
            vm.SelectedThrottleMode = vm.ThrottleModeOptions[1];
            Assert.Equal(DesyncStateMachine.State.Armed, controller.CurrentState); Assert.True(engine.ThrottleContinuous);
            vm.CycleProfileEnabled = true; vm.TriggerCycleCommand.Execute(null);
            Assert.True(engine.IsEffectActive);
            vm.CycleProfileEnabled = false;
            Assert.Equal(DesyncStateMachine.State.Armed, controller.CurrentState); Assert.True(engine.ThrottleEnabled); Assert.True(engine.ThrottleContinuous);
            Assert.True(engine.IsRunning);
        }
        finally { engine.Stop(); }
    });
    [Fact]
    public void LoadingStopsCaptureBeforeApplyingAllSettings() => WithVm((vm, engine, controller, dir) =>
    {
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        store.Save(new AppSettings { CycleProfileEnabled = false, ThrottleEnabled = true, ThrottleMode = ThrottleMode.Continuous, CustomFilter = "loopback", FilterMode = FilterMode.Custom });
        typeof(PacketEngine).GetField("_isRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, true);
        Assert.True(controller.TriggerCycle()); vm.LoadSettings();
        Assert.False(engine.IsRunning); Assert.False(vm.IsEngineRunning); Assert.Equal(DesyncStateMachine.State.Idle, controller.CurrentState);
        Assert.True(engine.ThrottleContinuous); Assert.Equal("loopback", vm.CustomFilterText);
    });
    [Fact]
    public void StoreClampsInvalidValuesAndRejectsInvalidEnumStrings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "DarkDesyncStore-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            File.WriteAllText(store.FilePath, "{\"DurationMs\":-1,\"RetentionMs\":99999,\"JitterMs\":99999,\"Chance\":-1,\"SpacingMs\":999,\"DropPercentage\":999,\"Hotkey\":\"invalid\",\"ThrottleMode\":99,\"FilterMode\":99}");
            var s = store.Load(); Assert.Equal(50, s.DurationMs); Assert.Equal(3000, s.RetentionMs); Assert.Equal(2000, s.JitterMs);
            Assert.Equal(0, s.Chance); Assert.Equal(200, s.SpacingMs); Assert.Equal(100, s.DropPercentage);
            Assert.Equal("Espaço", s.Hotkey); Assert.Equal(ThrottleMode.Hotkey, s.ThrottleMode); Assert.Equal(FilterMode.Outbound, s.FilterMode);
            store.Save(s); store.Save(s with { JitterMs = 1 }); Assert.Equal(1, store.Load().JitterMs); Assert.Single(Directory.GetFiles(dir));
            File.WriteAllText(store.FilePath, "{\"ThrottleMode\":\"invalid\"}"); Assert.Throws<System.Text.Json.JsonException>(() => store.Load());
        }
        finally { Directory.Delete(dir, true); }
    }
}
