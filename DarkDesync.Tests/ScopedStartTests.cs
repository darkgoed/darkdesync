using System.Diagnostics;
using System.IO;
using DarkDesync.Core;
using DarkDesync.Models;
using Xunit;

namespace DarkDesync.Tests;

public sealed class ScopedStartTests
{
    [Theory, MemberData(nameof(ScopedPulseTests.Modes), MemberType = typeof(ScopedPulseTests))]
    public void GlobalWithoutPid_ExplainsRequiredScope_AndNeverCaptures(DiagnosticMode mode) =>
        ConfigurationBehaviorTests.WithVm((vm, e, _, dir) =>
        {
            vm.SelectedFilterModeText = vm.FilterModeOptions[4];
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(mode);
            vm.ToggleEngineCommand.Execute(null);
            Assert.True(vm.IsLegacyGlobalSelected);
            Assert.Contains("Selecione", vm.StartStatusText);
            Assert.Contains("PID estrito", vm.StartStatusText);
            Assert.Equal("START", vm.EngineToggleText);
            Assert.False(e.IsCaptureRequested);
            Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
        });

    [Theory, MemberData(nameof(ScopedPulseTests.Modes), MemberType = typeof(ScopedPulseTests))]
    public void GlobalWithPid_MigratesOnlyUi_StartWaitsWithoutEffect_StopRevokes(DiagnosticMode mode) =>
        ConfigurationBehaviorTests.WithVm((vm, e, _, dir) =>
        {
            e.UdpFlowsOverride = () => [];
            e.EndpointsOverride = _ => [];
            vm.SelectedFilterModeText = vm.FilterModeOptions[4];
            vm.SaveSettingsCommand.Execute(null);
            byte[] saved = File.ReadAllBytes(Path.Combine(dir, "settings.json"));
            vm.SelectedProcess = new ProcessInfo { ProcessId = (uint)Environment.ProcessId };
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(mode);
            Assert.Equal(vm.FilterModeOptions[2], vm.SelectedFilterModeText);
            Assert.False(e.IsCaptureRequested);
            Assert.Equal(saved, File.ReadAllBytes(Path.Combine(dir, "settings.json")));
            if (mode == DiagnosticMode.ControlledPacketReordering)
            {
                vm.ToggleEngineCommand.Execute(null);
                Assert.Contains("Opt-in EXPERIMENTAL", vm.StartStatusText);
                Assert.False(e.IsCaptureRequested);
                vm.ExperimentalOptIn = true;
            }
            vm.ToggleEngineCommand.Execute(null);
            Assert.True(e.IsCaptureRequested);
            Assert.False(e.IsRunning);
            Assert.Equal("STOP", vm.EngineToggleText);
            Assert.Equal("AGUARDANDO", vm.StatusText);
            Assert.Contains("Aguardando conexões do processo", vm.StartStatusText);
            vm.TriggerCycleCommand.Execute(null);
            Assert.False(e.IsEffectActive);
            Assert.Equal("false", e.CurrentFilter);
            vm.ToggleEngineCommand.Execute(null);
            Assert.False(e.IsCaptureRequested);
            Assert.Equal("START", vm.EngineToggleText);
            Assert.Equal(DesyncStateMachine.State.Idle, e.StateMachine.Current);
        });

    [Theory, MemberData(nameof(ScopedPulseTests.Modes), MemberType = typeof(ScopedPulseTests))]
    public void SelectingPidAfterScopedChoice_AlsoMigratesGlobal(DiagnosticMode mode) =>
        ConfigurationBehaviorTests.WithVm((vm, e, _, _) =>
        {
            e.UdpFlowsOverride = () => []; e.EndpointsOverride = _ => [];
            vm.SelectedFilterModeText = vm.FilterModeOptions[4];
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(mode);
            vm.SelectedProcess = new ProcessInfo { ProcessId = (uint)Environment.ProcessId };
            Assert.False(vm.IsLegacyGlobalSelected);
            Assert.Equal(vm.FilterModeOptions[2], vm.SelectedFilterModeText);
            Assert.False(e.IsCaptureRequested);
        });

    [Theory, MemberData(nameof(ScopedPulseTests.Modes), MemberType = typeof(ScopedPulseTests))]
    public void DriverOpenFailure_VisibleWin32_RevokesPendingAndArmed_RetryStillScoped(DiagnosticMode mode) =>
        ConfigurationBehaviorTests.WithVm((vm, e, _, _) =>
        {
            e.UdpFlowsOverride = () => [DiagnosticModesTests.Endpoint() with { Protocol = 17 }];
            e.UdpBindingOverride = _ => true; e.EndpointsOverride = _ => [];
            var filters = new List<string>();
            e.NetworkOpenFailureOverride = filter => { filters.Add(filter); return 5; };
            vm.SelectedFilterModeText = vm.FilterModeOptions[2];
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(mode);
            vm.SelectedProcess = new ProcessInfo { ProcessId = (uint)Environment.ProcessId };
            vm.ExperimentalOptIn = true;
            for (int i = 0; i < 2; i++)
            {
                vm.ToggleEngineCommand.Execute(null);
                Assert.False(e.IsCaptureRequested); Assert.False(e.IsRunning);
                Assert.Equal(DesyncStateMachine.State.Idle, e.StateMachine.Current);
                Assert.Equal("START", vm.EngineToggleText);
                Assert.Contains("Win32 5", vm.StartStatusText);
            }
            Assert.Equal(2, filters.Count);
            Assert.All(filters, f => { Assert.Contains("ip.SrcAddr", f); Assert.Contains("udp.SrcPort", f); Assert.Contains("not impostor", f); });
            Assert.Equal("false", e.CurrentFilter);
        });

    [Fact]
    public void DeadPid_IsVisibleAndNeverWaitsOrArms() => ConfigurationBehaviorTests.WithVm((vm, e, _, _) =>
    {
        e.UdpFlowsOverride = () => []; e.EndpointsOverride = _ => [];
        vm.SelectedFilterModeText = vm.FilterModeOptions[2];
        vm.SelectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.PacketSizeAwareShaping);
        vm.SelectedProcess = new ProcessInfo { ProcessId = uint.MaxValue };
        vm.ToggleEngineCommand.Execute(null);
        Assert.Contains("Alvo indisponível", vm.StartStatusText);
        Assert.False(e.IsCaptureRequested); Assert.Equal("START", vm.EngineToggleText);
    });
}
