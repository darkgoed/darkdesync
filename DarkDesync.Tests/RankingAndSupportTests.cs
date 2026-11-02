using System.IO;
using DarkDesync.Core;
using DarkDesync.Models;
using DarkDesync.Services;
using Xunit;

namespace DarkDesync.Tests;

public sealed class RankingAndSupportTests
{
    [Fact]
    public void RankingIsPersonalAndStableIdentityRoundtrips()
    {
        Assert.Equal(new[] { DiagnosticMode.DualTokenBucket, DiagnosticMode.FlowAwareShaping, DiagnosticMode.OutboundFreeze }, ModeCatalog.PersonalRanking250ms);
        Assert.Equal(17, ModeCatalog.Choices.Count);
        Assert.Equal(new[] { "1º Dual Token Bucket", "2º Flow-Aware Shaping", "3º Outbound Freeze", "Bandwidth Shaping", "Inbound Bandwidth Shaping", "Correlated Inbound Jitter", "Packet Size-Aware Shaping", "Asymmetric Duplex Shaping", "Controlled Packet Reordering — EXPERIMENTAL" }, ModeCatalog.Choices.Keys.Take(9));
        foreach (var pair in ModeCatalog.Choices)
        {
            Assert.Equal(pair.Value, ModeCatalog.Parse(pair.Key));
            Assert.Equal(pair.Value, ModeCatalog.Parse(ModeCatalog.Name(pair.Value)));
            string alias = System.Text.RegularExpressions.Regex.Replace(pair.Key, @"^\d+º ", "");
            Assert.True(ModeCatalog.TryParse(alias, out var legacy));
            Assert.Equal(pair.Value, legacy);
        }
        Assert.False(ModeCatalog.TryParse("unknown", out _));
    }

    [Theory]
    [InlineData(175)]
    [InlineData(200)]
    [InlineData(250)]
    public void ExistingSettingsKeepEnumDurationAndFavorites(int duration) => ConfigurationBehaviorTests.WithVm((vm, e, _, dir) =>
    {
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var favorites = vm.Profiles.ToArray();
        foreach (var mode in ModeCatalog.Choices.Values)
        {
            store.Save(new AppSettings { DiagnosticMode = mode, DiagnosticDurationMs = duration });
            var before = File.ReadAllBytes(store.FilePath);
            vm.LoadSettings();
            Assert.Equal(ModeCatalog.Name(mode), vm.SelectedDiagnosticModeText);
            Assert.Equal(duration, vm.DiagnosticCycleDurationMs);
            Assert.Equal(before, File.ReadAllBytes(store.FilePath));
            vm.SaveSettingsCommand.Execute(null);
            Assert.Equal(mode, store.Load().DiagnosticMode);
            Assert.Equal(duration, store.Load().DiagnosticDurationMs);
            Assert.False(e.IsCaptureRequested);
        }
        Assert.Equal(favorites, vm.Profiles);
        Assert.Equal(3, vm.Profiles.Count); // Legacy intensity presets are unrelated to the four approved effects.
    });

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(87)]
    [InlineData(577)]
    public void FailedNativeStartKeepsCodeWithoutLeakingCustomFilterOrIdentity(int code) => ConfigurationBehaviorTests.WithVm((vm, e, _, _) =>
    {
        vm.SelectedFilterModeText = vm.FilterModeOptions[4];
        e.NetworkOpenFailureOverride = _ => code;
        vm.CustomFilterText = "secret-token 203.0.113.88:4567";
        vm.ToggleEngineCommand.Execute(null);
        Assert.False(e.IsRunning); Assert.False(e.IsCaptureRequested);
        Assert.Equal("START", vm.EngineToggleText);
        Assert.Contains($"Win32 {code}", vm.StartStatusText);
        Assert.Contains($"Win32={code}", vm.SupportDiagnosticText);
        Assert.DoesNotContain("203.0.113", vm.SupportDiagnosticText);
        Assert.DoesNotContain("secret-token", vm.SupportDiagnosticText);
        Assert.Contains(vm.Logs, l => l.Contains("START PRESSED"));
        Assert.Contains(vm.Logs, l => l.Contains("START RESULT") && l.Contains($"Win32 {code}"));
    });

    [Fact]
    public void DisabledEffectDoesNotMaskMissingPidOrPendingCapture() => ConfigurationBehaviorTests.WithVm((vm, e, _, _) =>
    {
        vm.CycleProfileEnabled = false;
        vm.ToggleEngineCommand.Execute(null);
        Assert.False(e.IsCaptureRequested);
        Assert.Contains("Selecione", vm.StartStatusText);
        Assert.Contains("PID obrigatório ausente", vm.SupportDiagnosticText);
        e.UdpFlowsOverride = () => []; e.EndpointsOverride = _ => [];
        vm.SelectedProcess = new ProcessInfo { ProcessId = (uint)Environment.ProcessId, ProcessName = "secret-process" };
        vm.ToggleEngineCommand.Execute(null);
        Assert.True(e.IsCaptureRequested); Assert.False(e.IsRunning);
        Assert.Equal("STOP", vm.EngineToggleText);
        Assert.Contains("Aguardando", vm.StartStatusText);
        Assert.Contains("Habilitado", vm.StartStatusText);
        Assert.DoesNotContain("secret-process", vm.SupportDiagnosticText);
        vm.ToggleEngineCommand.Execute(null);
        Assert.False(e.IsCaptureRequested);
        Assert.Contains("AGUARDANDO", vm.SupportDiagnosticText); // Last START survives STOP.
    });
}
