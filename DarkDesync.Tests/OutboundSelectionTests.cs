using System.IO;
using DarkDesync.Core;
using DarkDesync.Models;
using Xunit;

namespace DarkDesync.Tests;

public sealed class OutboundSelectionTests
{
    public static TheoryData<DiagnosticMode, FilterMode> Matrix
    {
        get
        {
            var data = new TheoryData<DiagnosticMode, FilterMode>();
            foreach (var effect in ModeCatalog.Choices.Values)
                foreach (var filter in Enum.GetValues<FilterMode>()) data.Add(effect, filter);
            return data;
        }
    }

    [Theory, MemberData(nameof(Matrix))]
    public void SelectionAndSettingsNeverChangeOnStart(DiagnosticMode mode, FilterMode filter) =>
        ConfigurationBehaviorTests.WithVm((vm, e, _, dir) =>
        {
            e.UdpFlowsOverride = () => []; e.EndpointsOverride = _ => [];
            vm.SelectedFilterModeText = vm.FilterModeOptions[(int)filter];
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(mode);
            vm.SelectedProcess = new ProcessInfo { ProcessId = (uint)Environment.ProcessId };
            vm.SaveSettingsCommand.Execute(null);
            byte[] saved = File.ReadAllBytes(Path.Combine(dir, "settings.json"));
            // GLOBAL approved favorites are covered by native tests, never open a
            // real driver in this deterministic matrix. New GLOBAL size lacks consent.
            bool blocked = PacketEngine.ProfileCompatibilityError(mode, filter) != null;
            if (filter != FilterMode.LegacyGlobalOutbound || blocked || mode == DiagnosticMode.PacketSizeAwareShaping)
            {
                vm.ToggleEngineCommand.Execute(null);
                if (blocked) { Assert.Contains("INCOMPATÍVEL", vm.StartStatusText); Assert.False(e.IsCaptureRequested); }
                else if (filter == FilterMode.LegacyGlobalOutbound) { Assert.Contains("Consentimento GLOBAL", vm.StartStatusText); Assert.False(e.IsCaptureRequested); }
                else if (mode == DiagnosticMode.ControlledPacketReordering) Assert.Contains("Opt-in", vm.StartStatusText);
                else Assert.True(e.IsCaptureRequested);
            }
            Assert.Equal(vm.FilterModeOptions[(int)filter], vm.SelectedFilterModeText);
            Assert.Equal(saved, File.ReadAllBytes(Path.Combine(dir, "settings.json")));
            e.Stop();
        });

    [Fact]
    public void GlobalSizePacesWithoutPid_ConsentRevokesAndDoesNotPersist() =>
        ConfigurationBehaviorTests.WithVm((vm, e, _, _) =>
        {
            vm.SelectedFilterModeText = vm.FilterModeOptions[4];
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.PacketSizeAwareShaping);
            vm.GlobalPacketSizeConsent = true;
            ArmedCaptureTests.ModelArm(e);
            Assert.True(e.ConfigureScopedProfile(DiagnosticMode.PacketSizeAwareShaping) == false); // Running profile already configured by VM.
            long now = ScopedPulseScheduler.T(1000);
            ArmedCaptureTests.Set(e, "_timestamp", (Func<long>)(() => now));
            var ids = new List<byte>();
            e.SendOverride = (p, n, _) => { ids.Add(System.Runtime.InteropServices.Marshal.ReadByte(p, (int)n - 1)); return true; };
            Assert.True(e.TriggerArmedEffect(DiagnosticMode.PacketSizeAwareShaping, 200));
            Assert.True(ScopedPulseTests.Capture(e, 1, true, size: 1000));
            Assert.True(ScopedPulseTests.Capture(e, 2, true, size: 100));
            ShapingEffectsTests.Release(e, now); Assert.Equal(new byte[] { 1 }, ids);
            now += ScopedPulseScheduler.T(100); ShapingEffectsTests.Release(e, now); Assert.Single(ids);
            now += ScopedPulseScheduler.T(25); ShapingEffectsTests.Release(e, now); Assert.Equal(new byte[] { 1, 2 }, ids);
            Assert.True(ScopedPulseTests.Capture(e, 3, true));
            vm.GlobalPacketSizeConsent = false;
            Assert.Equal(new byte[] { 1, 2, 3 }, ids); Assert.False(e.IsEffectActive);
            Assert.False(e.TriggerArmedEffect(DiagnosticMode.PacketSizeAwareShaping, 200));
            vm.GlobalPacketSizeConsent = true;
            vm.SaveSettingsCommand.Execute(null); vm.LoadSettingsCommand.Execute(null);
            Assert.False(vm.GlobalPacketSizeConsent); Assert.False(e.GlobalPacketSizeConsent);
            Assert.True(vm.IsGlobalPacketSizeSelected); Assert.False(e.IsCaptureRequested);
        });
}
