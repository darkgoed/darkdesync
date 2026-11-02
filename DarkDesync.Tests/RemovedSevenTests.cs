using System.IO;
using DarkDesync.Core;
using DarkDesync.Services;
using Xunit;

namespace DarkDesync.Tests;

public sealed class RemovedSevenTests
{
    public static IEnumerable<object[]> Replacements =>
        from removed in Enumerable.Range(31, 7).Select(id => (DiagnosticMode)id)
        from replacement in new[] { DiagnosticMode.DualTokenBucket, DiagnosticMode.FlowAwareShaping,
            DiagnosticMode.OutboundFreeze, DiagnosticMode.BandwidthShaping }
        select new object[] { removed, replacement };

    [Theory, MemberData(nameof(Replacements))]
    public void RemovedSettingsRemainReadOnlyUntilExplicitReplacementAndSave(DiagnosticMode removed, DiagnosticMode replacement)
        => ConfigurationBehaviorTests.WithVm((vm, engine, controller, dir) =>
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            store.Save(new AppSettings { DiagnosticMode = removed, DiagnosticDurationMs = 731 });
            byte[] original = File.ReadAllBytes(store.FilePath);
            Assert.Equal(removed, ModeCatalog.Parse(ModeCatalog.Name(removed)));
            Assert.Equal((int)removed, (int)store.Load().DiagnosticMode);
            int opens = 0; engine.NetworkOpenFailureOverride = _ => { opens++; return 5; };
            vm.LoadSettings();
            Assert.Null(vm.SelectedOutboundEffect);
            Assert.Equal(731, vm.DiagnosticCycleDurationMs);
            vm.SelectedOutboundEffect = ModeCatalog.Name(removed); // parser is not execution permission
            Assert.Null(vm.SelectedOutboundEffect);
            vm.ToggleEngineCommand.Execute(null); vm.TriggerCycleCommand.Execute(null);
            vm.SaveSettingsCommand.Execute(null);
            Assert.Equal(0, opens); Assert.False(engine.IsCaptureRequested);
            Assert.False(controller.TriggerCycle(removed, 800, legacyGlobal: true, globalConsent: true));
            ArmedCaptureTests.ModelArm(engine);
            engine.ActiveDiagnosticMode = removed;
            Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
            Assert.False(engine.TriggerArmedEffect(removed, 800));
            Assert.False(engine.IsEffectActive); engine.Stop();
            Assert.Equal(original, File.ReadAllBytes(store.FilePath));
            vm.SelectedOutboundEffect = ModeCatalog.Name(replacement);
            Assert.Equal(ModeCatalog.Name(replacement), vm.SelectedOutboundEffect);
            Assert.Equal(original, File.ReadAllBytes(store.FilePath));
            vm.ToggleEngineCommand.Execute(null);
            Assert.Equal(1, opens); Assert.Contains("Win32 5", vm.CompactStartErrorText);
            ArmedCaptureTests.ModelArm(engine);
            Assert.True(engine.TriggerArmedEffect(replacement, 800)); engine.Stop();
            vm.SaveSettingsCommand.Execute(null);
            Assert.Equal(replacement, store.Load().DiagnosticMode);
            Assert.Equal(731, store.Load().DiagnosticDurationMs);
            vm.ResetSettingsCommand.Execute(null);
            Assert.Equal(new[] { "1º Dual Token Bucket", "2º Flow-Aware Shaping", "3º Outbound Freeze", "Bandwidth Shaping" }, vm.DiagnosticModeOptions);
        }, outboundOnly: true);

    [Fact]
    public void AllRejectedIdentitiesCannotBypassProductionEngine()
        => ConfigurationBehaviorTests.WithVm((_, engine, controller, _) =>
        {
            foreach (var mode in Enum.GetValues<DiagnosticMode>().Where(m => m != DiagnosticMode.None && !ModeCatalog.OutboundChoices.Values.Contains(m)))
            {
                Assert.False(controller.TriggerDiagnosticCycle(mode, 800));
                ArmedCaptureTests.ModelArm(engine);
                engine.ActiveDiagnosticMode = mode;
                Assert.Equal(DiagnosticMode.None, engine.ActiveDiagnosticMode);
                Assert.False(engine.TriggerArmedEffect(mode, 800));
                Assert.False(engine.IsEffectActive); engine.Stop();
            }
        }, outboundOnly: true);
}
