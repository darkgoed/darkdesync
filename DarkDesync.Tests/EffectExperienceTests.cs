using System.IO;
using DarkDesync.Core;
using DarkDesync.Services;
using Xunit;

namespace DarkDesync.Tests;

public sealed class EffectExperienceTests
{
    [Theory]
    [InlineData(DiagnosticMode.DualTokenBucket, 800, "Agressivo", "#7F1D1D")]
    [InlineData(DiagnosticMode.FlowAwareShaping, 800, "Equilibrado", "#78350F")]
    [InlineData(DiagnosticMode.OutboundFreeze, 800, "Agressivo", "#7F1D1D")]
    [InlineData(DiagnosticMode.BandwidthShaping, 800, "Equilibrado", "#78350F")]
    [InlineData(DiagnosticMode.OutboundFreeze, 250, "Legit", "#14532D")]
    public void ExactPersonalObservationsOnly(DiagnosticMode mode, int ms, string label, string background)
    {
        var rating = EffectExperienceCatalog.Rating(mode, ms);
        Assert.Equal(label, rating.Label); Assert.Equal(background, rating.Background);
        Assert.Equal("Não avaliado", EffectExperienceCatalog.Rating(mode, ms - 1).Label);
        Assert.Equal("Não avaliado", EffectExperienceCatalog.Rating(mode, ms + 1).Label);
        if (mode == DiagnosticMode.OutboundFreeze && ms == 800) Assert.Contains("injogável", rating.Note);
    }

    [Theory, MemberData(nameof(OutboundEightEffectsTests.Modes), MemberType = typeof(OutboundEightEffectsTests))]
    public void NewEffectsNeverGetInventedRatings(DiagnosticMode mode)
    {
        foreach (int ms in new[] { 0, 175, 200, 250, 500, 800 })
        {
            var rating = EffectExperienceCatalog.Rating(mode, ms);
            Assert.Equal("Não avaliado", rating.Label); Assert.Equal("#374151", rating.Background);
        }
    }

    [Fact]
    public void OnlyTwoPrimaryStyles_ManualRequiresExplicitChoice()
        => ConfigurationBehaviorTests.WithVm((vm, engine, _, dir) =>
        {
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.DualTokenBucket);
            Assert.Equal(200, vm.DiagnosticCycleDurationMs); Assert.Equal("Teste manual", vm.SelectedPulseStyle);
            foreach (var row in new[] { ("Legit", 250), ("Agressivo", 800) })
            {
                vm.SelectedPulseStyle = row.Item1; Assert.Equal(row.Item2, vm.DiagnosticCycleDurationMs);
                Assert.Equal(ModeCatalog.Name(DiagnosticMode.DualTokenBucket), vm.SelectedDiagnosticModeText);
                Assert.Equal(EffectExperienceCatalog.Rating(DiagnosticMode.DualTokenBucket, row.Item2).Label, vm.EffectRatingText); Assert.False(engine.IsCaptureRequested);
            }
            vm.DiagnosticCycleDurationMs = 317; Assert.Equal(800, vm.DiagnosticCycleDurationMs); Assert.Equal("Agressivo", vm.SelectedPulseStyle);
            vm.IsManualDurationEnabled = true; vm.DiagnosticCycleDurationMs = 317;
            Assert.Equal("Teste manual", vm.SelectedPulseStyle); Assert.Equal(317, vm.DiagnosticCycleDurationMs);
            vm.SelectedPulseStyle = "Legit"; vm.DiagnosticCycleDurationMs = 275;
            Assert.Equal("Legit", vm.SelectedPulseStyle); Assert.Equal(250, vm.DiagnosticCycleDurationMs);
            Assert.Equal(new[] { "Legit", "Agressivo" }, vm.PulseStyleOptions);
            Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
            Assert.Null(EffectExperienceCatalog.FixedDuration(PulseStyle.Balanced));
        }, outboundOnly: true);

    [Theory]
    [InlineData("Teste manual", 175)]
    [InlineData("Legit", 250)]
    [InlineData("Agressivo", 800)]
    public void ExplicitSaveLoadDefaultsRoundtrip_LeavesChoiceFileAndCaptureAlone(string style, int ms)
        => ConfigurationBehaviorTests.WithVm((vm, engine, _, dir) =>
        {
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.OutboundFreeze);
            vm.SelectedPulseStyle = style; vm.DiagnosticCycleDurationMs = ms;
            vm.SaveSettingsCommand.Execute(null); var file = Path.Combine(dir, "settings.json"); byte[] original = File.ReadAllBytes(file);
            string rating = vm.EffectRatingText;
            vm.SelectedPulseStyle = "Teste manual"; vm.DiagnosticCycleDurationMs = 91;
            Assert.Equal(original, File.ReadAllBytes(file)); vm.LoadSettings();
            Assert.Equal(style, vm.SelectedPulseStyle); Assert.Equal(ms, vm.DiagnosticCycleDurationMs);
            Assert.Equal(rating, vm.EffectRatingText); Assert.Equal(ModeCatalog.Name(DiagnosticMode.OutboundFreeze), vm.SelectedDiagnosticModeText);
            Assert.Equal(original, File.ReadAllBytes(file)); Assert.False(engine.IsCaptureRequested);
            vm.ResetSettingsCommand.Execute(null);
            Assert.Equal(200, vm.DiagnosticCycleDurationMs); Assert.Equal("Teste manual", vm.SelectedPulseStyle);
            Assert.Equal("Não avaliado", vm.EffectRatingText); Assert.Equal(original, File.ReadAllBytes(file));
        }, outboundOnly: true);

    [Fact]
    public void OldSettingsKeepDurationAndUnavailableModeWithoutWritingOrInventingStyle()
        => ConfigurationBehaviorTests.WithVm((vm, engine, _, dir) =>
        {
            Directory.CreateDirectory(dir); string file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, "{\"DiagnosticMode\":\"PacketSizeAwareShaping\",\"DiagnosticDurationMs\":731}");
            byte[] original = File.ReadAllBytes(file); vm.LoadSettings();
            Assert.Null(vm.SelectedOutboundEffect); Assert.Equal(731, vm.DiagnosticCycleDurationMs);
            Assert.Equal("Teste manual", vm.SelectedPulseStyle); Assert.Equal("Não avaliado", vm.EffectRatingText);
            Assert.Equal(original, File.ReadAllBytes(file)); Assert.False(engine.IsCaptureRequested);
            Assert.Equal(PulseStyle.Custom, (new AppSettings { PulseStyle = PulseStyle.Legitimate, DiagnosticDurationMs = 731 }).Validate().PulseStyle);
        }, outboundOnly: true);

    [Fact]
    public void EveryChipHasTextAndWcagNormalTextContrast()
    {
        double Lum(string hex)
        {
            double Component(int offset)
            {
                double v = Convert.ToInt32(hex.Substring(offset, 2), 16) / 255d;
                return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
            }
            return .2126 * Component(1) + .7152 * Component(3) + .0722 * Component(5);
        }
        foreach (var mode in ModeCatalog.OutboundChoices.Values)
            foreach (int ms in new[] { 200, 250, 800 })
            {
                var r = EffectExperienceCatalog.Rating(mode, ms);
                double a = Lum(r.Foreground), b = Lum(r.Background);
                Assert.True((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5);
                Assert.False(string.IsNullOrWhiteSpace(r.Label));
            }
    }

    [Fact]
    public void StyleOrManualDurationChangeAffectsOnlyNextPulse()
        => ConfigurationBehaviorTests.WithVm((vm, engine, _, _) =>
        {
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.DualTokenBucket);
            ArmedCaptureTests.ModelArm(engine);
            Assert.True(engine.TriggerArmedEffect(DiagnosticMode.DualTokenBucket, 800));
            vm.SelectedPulseStyle = "Legit";
            Assert.True(engine.IsEffectActive); Assert.True(engine.IsCaptureRequested); Assert.True(engine.IsRunning);
            Assert.Equal(DiagnosticMode.DualTokenBucket, engine.ActiveDiagnosticMode);
            Assert.False(engine.TriggerArmedEffect(DiagnosticMode.DualTokenBucket, 250));
            vm.IsManualDurationEnabled = true; vm.DiagnosticCycleDurationMs = 317;
            Assert.True(engine.IsEffectActive); Assert.Equal("Teste manual", vm.SelectedPulseStyle);
            Assert.Equal(ModeCatalog.Name(DiagnosticMode.DualTokenBucket), vm.SelectedDiagnosticModeText);
            engine.Stop();
        }, outboundOnly: true);
}
