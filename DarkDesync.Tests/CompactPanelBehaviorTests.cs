using System.Diagnostics;
using System.IO;
using System.Reflection;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public sealed class CompactPanelBehaviorTests
{
    [Theory]
    [InlineData(2)] [InlineData(5)] [InlineData(87)] [InlineData(577)]
    public void StartErrorIsShortSanitizedTransient_AndInternalSnapshotRemains(int code)
        => ConfigurationBehaviorTests.WithVm((vm, engine, _, _) =>
        {
            vm.CustomFilterText = "203.0.113.88 secret-token";
            engine.NetworkOpenFailureOverride = f => { Assert.Equal("outbound", f); return code; };
            vm.ToggleEngineCommand.Execute(null);
            Assert.Contains("Win32 " + code, vm.CompactStartErrorText);
            Assert.True(vm.CompactStartErrorText.Length < 110);
            Assert.DoesNotContain("secret-token", vm.CompactStartErrorText); Assert.DoesNotContain("203.0.113", vm.CompactStartErrorText);
            Assert.Contains("Win32=" + code, vm.SupportDiagnosticText); Assert.False(engine.IsCaptureRequested);
            typeof(DarkDesync.ViewModels.MainViewModel).GetField("_startErrorAt", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(vm, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 16);
            Assert.Equal("", vm.CompactStartErrorText); Assert.Contains("Win32=" + code, vm.SupportDiagnosticText);
        }, outboundOnly: true);

    [Fact]
    public void LegacyBalancedStyleBecomesManual_ReadOnlyWithoutChangingDurationOrEffect()
        => ConfigurationBehaviorTests.WithVm((vm, engine, _, dir) =>
        {
            Directory.CreateDirectory(dir); string path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"PulseStyle\":\"Balanced\",\"DiagnosticDurationMs\":347,\"DiagnosticMode\":\"FlowAwareShaping\"}");
            byte[] before = File.ReadAllBytes(path); vm.LoadSettings();
            Assert.Equal("Teste manual", vm.SelectedPulseStyle); Assert.Equal(347, vm.DiagnosticCycleDurationMs);
            Assert.Equal(ModeCatalog.Name(DiagnosticMode.FlowAwareShaping), vm.SelectedDiagnosticModeText);
            Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(engine.IsCaptureRequested);
        }, outboundOnly: true);

    [Fact]
    public void PresetDuringPulseDoesNotChangeDeadlineOrReleasePendingPackets()
        => ConfigurationBehaviorTests.WithVm((vm, engine, _, _) =>
        {
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.TwoPhaseDrain);
            ArmedCaptureTests.ModelArm(engine); Assert.True(engine.TriggerArmedEffect(DiagnosticMode.TwoPhaseDrain, 800));
            Assert.True(ShapingEffectsTests.Capture(engine, 19, protocol: 17));
            var field = typeof(PacketEngine).GetField("_effectDeadline", BindingFlags.NonPublic | BindingFlags.Instance)!;
            object? deadline = field.GetValue(engine); long released = engine.ScheduledPacketsReleased;
            vm.SelectPulseStyleCommand.Execute("Legit");
            Assert.Equal(250, vm.DiagnosticCycleDurationMs); Assert.True(engine.IsEffectActive);
            Assert.Equal(deadline, field.GetValue(engine)); Assert.Equal(released, engine.ScheduledPacketsReleased);
            vm.SelectPulseStyleCommand.Execute("Agressivo"); Assert.Equal(deadline, field.GetValue(engine));
            engine.Stop(); Assert.Equal(released + 1, engine.ScheduledPacketsReleased);
        }, outboundOnly: true);

    [Fact]
    public void AssessmentsCanBeEditedAsDataWithoutRecompilationOrChangingPulseSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), "DarkDesync-assessments-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "[{\"Mode\":\"TimeSlotBursting\",\"DurationMs\":317,\"Category\":\"Balanced\",\"Note\":\"Avaliação pessoal adicional\",\"PreferredDurationMs\":250}]");
            var store = EffectAssessmentStore.Load(path); var entry = store.Find(DiagnosticMode.TimeSlotBursting, 317)!;
            Assert.Equal(AssessmentCategory.Balanced, entry.Category); Assert.Equal(250, entry.PreferredDurationMs);
            Assert.Null(store.Find(DiagnosticMode.TimeSlotBursting, 250));
            File.WriteAllText(path, "[]"); Assert.Null(EffectAssessmentStore.Load(path).Find(DiagnosticMode.TimeSlotBursting, 317));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("[{\"Mode\":999,\"DurationMs\":200,\"Category\":0,\"Note\":\"x\"}]")]
    [InlineData("[{\"Mode\":23,\"DurationMs\":801,\"Category\":0,\"Note\":\"x\"}]")]
    [InlineData("[{\"Mode\":23,\"DurationMs\":200,\"Category\":999,\"Note\":\"x\"}]")]
    [InlineData("[{\"Mode\":23,\"DurationMs\":200,\"Category\":0,\"Note\":\"x\"},{\"Mode\":23,\"DurationMs\":200,\"Category\":0,\"Note\":\"x\"}]")]
    public void UnsafeOrAmbiguousAssessmentDataIsRejected(string json) => Assert.Throws<InvalidDataException>(() => EffectAssessmentStore.Parse(json));

    [Fact]
    public void InvalidOrMissingEditableCatalogFallsBackWithoutWriting()
    {
        string path = Path.Combine(Path.GetTempPath(), "DarkDesync-bad-assessments-" + Guid.NewGuid() + ".json");
        try
        {
            foreach (string json in new[] { "not JSON", "[{\"Mode\":999,\"DurationMs\":200,\"Category\":0,\"Note\":\"x\"}]" })
            {
                File.WriteAllText(path, json);
                Assert.Equal(AssessmentCategory.Balanced, EffectAssessmentStore.LoadOrEmbedded(path).Find(DiagnosticMode.FlowAwareShaping, 800)!.Category);
                Assert.Equal(json, File.ReadAllText(path));
            }
            File.Delete(path);
            Assert.Equal(AssessmentCategory.Legit, EffectAssessmentStore.LoadOrEmbedded(path).Find(DiagnosticMode.OutboundFreeze, 250)!.Category);
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }
}
