using System.IO;
using DarkDesync.Core;
using DarkDesync.Services;
using Xunit;

namespace DarkDesync.Tests;

public sealed class RetiredEffectTests
{
    [Theory, MemberData(nameof(OutboundEightEffectsTests.Modes), MemberType = typeof(OutboundEightEffectsTests))]
    public void EveryRejectedSavedModeIsReadOnlyUnavailableAndCannotStartPulseOrSaveUntilReplacement(DiagnosticMode mode)
        => ConfigurationBehaviorTests.WithVm((vm, engine, _, dir) =>
        {
            Directory.CreateDirectory(dir); string path = Path.Combine(dir, "settings.json"); var store = new SettingsStore(path);
            store.Save(new AppSettings { DiagnosticMode = mode, DiagnosticDurationMs = 731 }); byte[] before = File.ReadAllBytes(path);
            int opens = 0; engine.NetworkOpenFailureOverride = _ => { opens++; return 5; };
            ArmedCaptureTests.ModelArm(engine);
            Assert.True(engine.TriggerArmedEffect(DiagnosticMode.OutboundFreeze, 800));
            Assert.True(ShapingEffectsTests.Capture(engine, 29, protocol: 17));
            vm.LoadSettings(); Assert.Null(vm.SelectedOutboundEffect); Assert.Equal(731, vm.DiagnosticCycleDurationMs);
            Assert.False(engine.IsEffectActive); Assert.False(engine.IsCaptureRequested);
            long afterLoad = engine.PacketsPassedThrough; engine.FlushThrottleQueue(); Assert.Equal(afterLoad, engine.PacketsPassedThrough);
            Assert.Contains("indisponível", vm.CompactStartErrorText);
            vm.ToggleEngineCommand.Execute(null); vm.TriggerCycleCommand.Execute(null); vm.SaveSettingsCommand.Execute(null);
            Assert.Equal(0, opens); Assert.False(engine.IsCaptureRequested); Assert.False(engine.IsEffectActive);
            Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(mode, store.Load().DiagnosticMode);
            // Defense in depth: a previously armed epoch cannot bypass rejection.
            ArmedCaptureTests.ModelArm(engine); vm.TriggerCycleCommand.Execute(null);
            Assert.False(engine.IsEffectActive); Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode); engine.Stop();
            vm.SelectedOutboundEffect = ModeCatalog.Name(DiagnosticMode.FlowAwareShaping); vm.SaveSettingsCommand.Execute(null);
            Assert.Equal(DiagnosticMode.FlowAwareShaping, store.Load().DiagnosticMode);
            Assert.Equal(731, store.Load().DiagnosticDurationMs);
            vm.ResetSettingsCommand.Execute(null); Assert.Equal(11, vm.DiagnosticModeOptions.Count);
            Assert.DoesNotContain(ModeCatalog.Name(mode), vm.DiagnosticModeOptions);
            Assert.Equal(mode, ModeCatalog.Parse(ModeCatalog.Name(mode))); // IDs/parser remain legacy-compatible.
        }, outboundOnly: true);
}
