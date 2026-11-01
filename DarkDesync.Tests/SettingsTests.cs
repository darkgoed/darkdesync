using System.IO;
using System.Reflection;
using DarkDesync.ViewModels;
using DarkDesync.Services;
using DarkDesync.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;
public sealed class SettingsTests
{
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    [Fact]
    public void SettingsCommands_RoundTripWithoutStartingCapture() => Sta(() =>
    {
        string dir = Path.Combine(Path.GetTempPath(), "DarkDesyncTests-" + Guid.NewGuid());
        try
        {
            var storeType = typeof(MainViewModel).Assembly.GetType("DarkDesync.Services.SettingsStore");
            Assert.NotNull(storeType);
            var store = Activator.CreateInstance(storeType, Path.Combine(dir, "settings.json"));
            using var controller = new DesyncController(NullLogger<DesyncController>.Instance, new DesyncStateMachine());
            using var vm = (MainViewModel)Activator.CreateInstance(typeof(MainViewModel),
                new ProcessService(NullLogger<ProcessService>.Instance), new HotkeyService(NullLogger<HotkeyService>.Instance), controller, null, store)!;
            void Set(string name, object value) => typeof(MainViewModel).GetProperty(name)!.SetValue(vm, value);
            void Command(string name) => ((System.Windows.Input.ICommand)typeof(MainViewModel).GetProperty(name)!.GetValue(vm)!).Execute(null);
            Set("CycleProfileEnabled", false); Set("SelectedThrottleMode", "Contínuo (captura ligada)");
            vm.ThrottleEnabled = true; vm.ThrottleJitterMs = 777; vm.CustomDurationMs = 1234;
            vm.ThrottleChance = 43; vm.ThrottleTimeframeMs = 789; vm.ThrottleReleaseDelayMs = 31;
            vm.DropPercentage = 19; vm.SelectedHotkeyName = "F6"; vm.OnlyTargetForeground = false;
            vm.SelectedFilterModeText = "Personalizado"; vm.CustomFilterText = "loopback";
            Command("SaveSettingsCommand");
            Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
            Command("ResetSettingsCommand"); Assert.Equal(0, vm.ThrottleJitterMs);
            Command("LoadSettingsCommand");
            Assert.Equal(777, vm.ThrottleJitterMs); Assert.Equal(1234, vm.CustomDurationMs);
            Assert.Equal(43, vm.ThrottleChance); Assert.Equal(789, vm.ThrottleTimeframeMs); Assert.Equal(31, vm.ThrottleReleaseDelayMs);
            Assert.Equal(19, vm.DropPercentage); Assert.Equal("F6", vm.SelectedHotkeyName); Assert.False(vm.OnlyTargetForeground);
            Assert.Equal("loopback", vm.CustomFilterText); Assert.False(vm.IsEngineRunning);
            Assert.False((bool)typeof(MainViewModel).GetProperty("CycleProfileEnabled")!.GetValue(vm)!);
            Assert.Equal("Contínuo (captura ligada)", typeof(MainViewModel).GetProperty("SelectedThrottleMode")!.GetValue(vm));
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{broken");
            Command("LoadSettingsCommand"); Assert.Equal(777, vm.ThrottleJitterMs);
            Assert.Contains("Não foi possível", (string)typeof(MainViewModel).GetProperty("SettingsStatus")!.GetValue(vm)!);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Version\":99}");
            Command("LoadSettingsCommand"); Assert.Equal(777, vm.ThrottleJitterMs);
            Assert.Contains("Não foi possível", vm.SettingsStatus);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    });
}
