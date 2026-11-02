using System.IO;
using DarkDesync.Core;
using DarkDesync.Models;
using DarkDesync.Services;
using Xunit;

namespace DarkDesync.Tests;

public sealed class OutboundInterfaceTests
{
    [Fact]
    public void ProductionDefaultsHaveNoTargetOrProtocolPrerequisites() => ConfigurationBehaviorTests.WithVm((vm, e, _, _) =>
    {
        Assert.Equal(12, vm.DiagnosticModeOptions.Count);
        Assert.Equal(ModeCatalog.OutboundChoices.Keys, vm.DiagnosticModeOptions);
        Assert.DoesNotContain("Packet Size-Aware Shaping", vm.DiagnosticModeOptions);
        Assert.Equal(200, vm.DiagnosticCycleDurationMs);
        Assert.Equal(ModeCatalog.Name(DiagnosticMode.OutboundFreeze), vm.SelectedDiagnosticModeText);
        Assert.True(vm.IsLegacyGlobalSelected);
        Assert.Equal(FilterMode.LegacyGlobalOutbound, e.ConfiguredFilterMode);
        Assert.Null(vm.SelectedProcess); Assert.Empty(vm.Processes); Assert.False(vm.OnlyTargetForeground);
        Assert.False(e.IsCaptureRequested); Assert.False(e.IsRunning);
        Assert.Equal("PARADO / PAUSADO", vm.CaptureStateText);
        foreach (var oldFilter in vm.FilterModeOptions) vm.SelectedFilterModeText = oldFilter;
        vm.SelectedProcess = new ProcessInfo { ProcessId = 123, ProcessName = "private-process-token" };
        vm.OnlyTargetForeground = true;
        Assert.True(vm.IsLegacyGlobalSelected); Assert.Null(vm.SelectedProcess); Assert.False(vm.OnlyTargetForeground);
        Assert.DoesNotContain("PID", vm.StartStatusText);
        Assert.DoesNotContain("TCP", vm.NetworkDiagnosticsText); Assert.DoesNotContain("UDP", vm.NetworkDiagnosticsText);
    }, outboundOnly: true);

    [Fact]
    public void EveryVisibleEffectReachesLiteralOutboundWithoutPid_ConsentOnlyForSize() => ConfigurationBehaviorTests.WithVm((vm, e, _, _) =>
    {
        int opens = 0;
        e.NetworkOpenFailureOverride = filter => { Assert.Equal("outbound", filter); opens++; return 5; };
        foreach (var mode in ModeCatalog.OutboundChoices.Values)
        {
            vm.SelectedDiagnosticModeText = ModeCatalog.Name(mode);
            vm.CycleProfileEnabled = true;
            int before = opens;
            if (mode == DiagnosticMode.PacketSizeAwareShaping)
            {
                vm.ToggleEngineCommand.Execute(null);
                Assert.Equal(before, opens); Assert.Contains("Consentimento", vm.StartStatusText);
                vm.GlobalPacketSizeConsent = true;
            }
            vm.ToggleEngineCommand.Execute(null);
            Assert.Equal(before + 1, opens); // Injected refusal, not a fabricated driver success.
            Assert.Contains("Win32 5", vm.StartStatusText);
            Assert.DoesNotContain("Copiar diagnóstico", vm.StartStatusText);
            Assert.Contains("Win32 5", vm.CompactStartErrorText);
            Assert.Equal("PARADO / PAUSADO", vm.CaptureStateText);
            Assert.False(e.IsCaptureRequested); Assert.False(e.IsRunning);
            Assert.DoesNotContain("PID", vm.SupportDiagnosticText);
            Assert.Contains("Win32=5", vm.SupportDiagnosticText);
        }
    }, outboundOnly: true);

    public static IEnumerable<object[]> LegacySettings =>
        from mode in Enum.GetValues<DiagnosticMode>()
        from filter in Enum.GetValues<FilterMode>()
        select new object[] { mode, filter };

    [Theory, MemberData(nameof(LegacySettings))]
    public void OldSettingsLoadReadOnlyAndUnsupportedEnumsRoundtripWithoutArming(DiagnosticMode mode, FilterMode filter) =>
        ConfigurationBehaviorTests.WithVm((vm, e, _, dir) =>
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            store.Save(new AppSettings { DiagnosticMode = mode, FilterMode = filter, DiagnosticDurationMs = 175,
                OnlyTargetForeground = true, CustomFilter = "203.0.113.88:4567 secret-token" });
            var bytes = File.ReadAllBytes(store.FilePath);
            vm.LoadSettings();
            Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
            Assert.Equal(175, vm.DiagnosticCycleDurationMs);
            Assert.True(vm.IsLegacyGlobalSelected); Assert.False(vm.OnlyTargetForeground); Assert.Null(vm.SelectedProcess);
            Assert.False(e.IsCaptureRequested); Assert.False(e.IsRunning);
            Assert.False(vm.GlobalPacketSizeConsent); Assert.False(vm.ExperimentalOptIn);
            bool unavailable = mode != DiagnosticMode.None && !ModeCatalog.OutboundChoices.Values.Contains(mode);
            if (unavailable)
            {
                Assert.Contains("indisponível", vm.SettingsStatus);
                Assert.Null(vm.SelectedOutboundEffect);
                Assert.DoesNotContain(vm.SelectedDiagnosticModeText, vm.DiagnosticModeOptions);
                int attempts = 0; e.NetworkOpenFailureOverride = _ => { attempts++; return 5; };
                vm.ToggleEngineCommand.Execute(null); vm.TriggerCycleCommand.Execute(null);
                Assert.Equal(0, attempts); Assert.Contains("INCOMPATÍVEL", vm.StartStatusText);
                Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
                vm.SaveSettingsCommand.Execute(null);
                Assert.Equal(mode, store.Load().DiagnosticMode);
                vm.SelectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.OutboundFreeze);
                Assert.Equal(ModeCatalog.Name(DiagnosticMode.OutboundFreeze), vm.SelectedOutboundEffect);
                vm.SaveSettingsCommand.Execute(null);
                Assert.Equal(DiagnosticMode.OutboundFreeze, store.Load().DiagnosticMode);
            }
            Assert.DoesNotContain("203.0.113", vm.SupportDiagnosticText);
            Assert.DoesNotContain("secret-token", vm.SupportDiagnosticText);
            Assert.DoesNotContain("PID", vm.SupportDiagnosticText);
        }, outboundOnly: true);
}
