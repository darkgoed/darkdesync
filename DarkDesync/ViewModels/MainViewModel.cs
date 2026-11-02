using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DarkDesync.Core;
using DarkDesync.Models;
using DarkDesync.Services;

namespace DarkDesync.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsStore _settingsStore;
    private readonly bool _outboundOnly;
    private DiagnosticMode? _unavailableSavedMode;
    private readonly ProcessService _processService;
    public ObservableCollection<string> ThrottleModeOptions { get; } = new() { "No atalho / ciclo", "Contínuo (captura ligada)" };
    private bool _cycleProfileEnabled = true;
    public bool CycleProfileEnabled
    {
        get => _cycleProfileEnabled;
        set
        {
            if (!SetProperty(ref _cycleProfileEnabled, value)) return;
            if (_packetEngine != null) _packetEngine.CycleDropEnabled = value;
            if (!value) CancelCurrentPulse();
            RefreshNetworkDiagnostics();
        }
    }
    private string _selectedThrottleMode = "No atalho / ciclo";
    public string SelectedThrottleMode
    {
        get => _selectedThrottleMode;
        set
        {
            if (!ThrottleModeOptions.Contains(value) || !SetProperty(ref _selectedThrottleMode, value)) return;
            if (_packetEngine != null) _packetEngine.ThrottleContinuous = value == ThrottleModeOptions[1];
            CancelCurrentPulse();
        }
    }
    private string _settingsStatus = "Salve para reutilizar ao abrir. O alvo não é salvo.";
    public string SettingsStatus { get => _settingsStatus; private set { if (SetProperty(ref _settingsStatus, value)) OnPropertyChanged(nameof(CompactSettingsStatus)); } }
    public string CompactSettingsStatus => _settingsStatus.StartsWith("Configuração salva") ? "Configuração salva." :
        _settingsStatus.StartsWith("Não foi possível") ? "Não foi possível carregar ou salvar a configuração." :
        _settingsStatus.StartsWith("Configuração carregada") ? "Configuração carregada · sem iniciar captura." : "Salvar é explícito. START apenas arma; Espaço aplica o pulso.";
    public string SettingsPath => _settingsStore.FilePath;
    public ICommand SaveSettingsCommand { get; }
    public ICommand LoadSettingsCommand { get; }
    public ICommand ResetSettingsCommand { get; }
    private readonly HotkeyService _hotkeyService;
    private readonly PacketEngine? _packetEngine;
    private readonly DesyncController _controller;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statsTimer;

    // Observable collections
    public ObservableCollection<ProcessInfo> Processes { get; } = new();
    public ObservableCollection<IntensityProfile> Profiles { get; } = new();
    public ObservableCollection<CycleRecord> History { get; } = new();
    public ObservableCollection<string> Logs { get; } = new();

    public Dictionary<string, int> AvailableHotkeys { get; }

    public ObservableCollection<string> FilterModeOptions { get; } = new()
    {
        "Saída do alvo (IPv4, TCP/UDP)",
        "Entrada + saída do alvo (IPv4, TCP/UDP)",
        "Conexões verificadas da aplicação",
        "Personalizado",
        "Outbound legado — todos os aplicativos (global)"
    };

    private string _selectedFilterModeText = "Saída do alvo (IPv4, TCP/UDP)";
    public string SelectedFilterModeText
    {
        get => _selectedFilterModeText;
        set
        {
            if (_outboundOnly && value != FilterModeOptions[4]) return;
            if (SetProperty(ref _selectedFilterModeText, value))
            {
                GlobalPacketSizeConsent = false;
                OnPropertyChanged(nameof(IsGlobalPacketSizeSelected));
                OnPropertyChanged(nameof(IsLegacyGlobalSelected));
                OnPropertyChanged(nameof(IsStrictCaptureMode));
                OnPropertyChanged(nameof(IsGlobalDropVisible));
                OnPropertyChanged(nameof(IsDiagnosticModeSelected));
                OnPropertyChanged(nameof(IsLegacyModeSelected));
                OnPropertyChanged(nameof(EngineToggleText));
                ApplyFilterMode();
            }
        }
    }

    private string _customFilterText = "outbound";
    public string CustomFilterText
    {
        get => _customFilterText;
        set
        {
            if (SetProperty(ref _customFilterText, value))
            {
                if (GetFilterMode() == FilterMode.Custom)
                {
                    ApplyFilterMode();
                }
            }
        }
    }

    public bool IsCustomFilterVisible => GetFilterMode() == FilterMode.Custom;
    public bool IsLegacyGlobalSelected => GetFilterMode() == FilterMode.LegacyGlobalOutbound;
    public bool IsStrictCaptureMode => !IsLegacyGlobalSelected;
    public bool IsGlobalDropVisible => _selectedDiagnosticModeText == "Drop (%)";

    // Properties
    private ProcessInfo? _selectedProcess;
    public ProcessInfo? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (_outboundOnly && value != null) return;
            if (SetProperty(ref _selectedProcess, value))
            {
                _controller.TargetProcess = value;
                if (value != null && OnlyTargetForeground)
                {
                    _hotkeyService.SetTargetWindow(value.MainWindowHandle);
                }
                else
                {
                    _hotkeyService.SetTargetWindow(IntPtr.Zero);
                }

                AddLog($"[PROCESSO] Alvo: {value?.ProcessName ?? "Nenhum"} (PID: {value?.ProcessId ?? 0})");

                ApplyFilterMode();
            }
        }
    }

    private IntensityProfile _selectedProfile;
    public IntensityProfile SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                _controller.ActiveProfile = new IntensityProfile { Name = value.Name, DurationMs = value.DurationMs, DropPercentage = value.DropPercentage, Color = value.Color };
                CustomDurationMs = value.DurationMs;
                DropPercentage = value.DropPercentage;
                AddLog($"[PERFIL] {value.Name} ({value.DurationMs}ms, {value.DropPercentage}%)");
            }
        }
    }

    private int _customDurationMs = 800;
    public int CustomDurationMs
    {
        get => _customDurationMs;
        set
        {
            if (SetProperty(ref _customDurationMs, Math.Clamp(value, 50, 10000)))
            {
                if (SelectedProfile != null)
                {
                    _controller.ActiveProfile.DurationMs = _customDurationMs;
                }
            }
        }
    }

    private int _dropPercentage = 100;
    public int DropPercentage
    {
        get => _dropPercentage;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _dropPercentage, clamped))
            {
                if (SelectedProfile != null)
                {
                    _controller.ActiveProfile.DropPercentage = clamped;
                }
                _packetEngine?.SetDropPercentage(clamped);
            }
        }
    }

    // Throttle Properties (Clumsy style: Timeframe, Chance, Speed)
    private bool _throttleEnabled = false;
    public bool ThrottleEnabled
    {
        get => _throttleEnabled;
        set
        {
            if (SetProperty(ref _throttleEnabled, value))
            {
                if (!value) CancelCurrentPulse();
                if (_packetEngine != null)
                {
                    _packetEngine.ThrottleEnabled = value;
                }
                AddLog($"[THROTTLE] {(value ? "Habilitado" : "Desabilitado")}");

            }
        }
    }

    private int _throttleTimeframeMs = 1000;
    public int ThrottleTimeframeMs
    {
        get => _throttleTimeframeMs;
        set
        {
            int clamped = Math.Clamp(value, 10, 3000);
            if (SetProperty(ref _throttleTimeframeMs, clamped))
            {
                if (_packetEngine != null)
                {
                    _packetEngine.ThrottleTimeframeMs = clamped;
                }
            }
        }
    }

    private int _throttleChance = 100;
    public int ThrottleChance
    {
        get => _throttleChance;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _throttleChance, clamped))
            {
                if (_packetEngine != null)
                {
                    _packetEngine.ThrottleChance = clamped;
                }
            }
        }
    }

    private int _throttleReleaseDelayMs = 0;
    public int ThrottleReleaseDelayMs
    {
        get => _throttleReleaseDelayMs;
        set
        {
            int clamped = Math.Clamp(value, 0, 200);
            if (SetProperty(ref _throttleReleaseDelayMs, clamped))
            {
                if (_packetEngine != null)
                {
                    _packetEngine.ThrottleReleaseDelayMs = clamped;
                }
                OnPropertyChanged(nameof(ThrottleSpeedDisplay));
            }
        }
    }

    private int _throttleJitterMs;
    public int ThrottleJitterMs
    {
        get => _throttleJitterMs;
        set
        {
            int clamped = Math.Clamp(value, 0, 2000);
            if (SetProperty(ref _throttleJitterMs, clamped) && _packetEngine != null)
                _packetEngine.ThrottleJitterMs = clamped;
        }
    }

    public string ThrottleSpeedDisplay => _throttleReleaseDelayMs == 0
        ? "0 ms • rajada"
        : $"{_throttleReleaseDelayMs} ms entre pacotes";

    private string _selectedHotkeyName = "Espaço";
    public string SelectedHotkeyName
    {
        get => _selectedHotkeyName;
        set
        {
            if (SetProperty(ref _selectedHotkeyName, value))
            {
                if (AvailableHotkeys.TryGetValue(value, out int vk))
                {
                    _hotkeyService.SetHotkey(vk);
                    AddLog($"[ATALHO] Tecla de acionamento: {value}");
                }
            }
        }
    }

    // Diagnostic mode selection
    private string _selectedDiagnosticModeText = ModeCatalog.Name(DiagnosticMode.OutboundFreeze);
    public List<string> DiagnosticModeOptions { get; }
    // WPF rejects an out-of-list SelectedItem by retaining the previous value.
    // An explicit null clears the selector for an incompatible saved enum instead
    // of falsely displaying an old supported choice while START is refused.
    public string? SelectedOutboundEffect
    {
        get => _unavailableSavedMode != null ? null : SelectedDiagnosticModeText;
        set { if (value != null) SelectedDiagnosticModeText = value; }
    }
    public bool IsExperimentalSelected => ModeCatalog.Parse(_selectedDiagnosticModeText) == DiagnosticMode.ControlledPacketReordering;
    private bool _experimentalOptIn;
    public bool ExperimentalOptIn
    {
        get => _experimentalOptIn;
        set
        {
            if (!SetProperty(ref _experimentalOptIn, value)) return;
            CancelCurrentPulse();
            if (_packetEngine != null) _packetEngine.ExperimentalReorderingConsent = value;
            RefreshNetworkDiagnostics();
        }
    }

    public string SelectedDiagnosticModeText
    {
        get => _selectedDiagnosticModeText;
        set
        {
            if (value == null || !ModeCatalog.TryParse(value, out var selectedMode)) return;
            if (_outboundOnly && !ModeCatalog.OutboundChoices.Values.Contains(selectedMode)) return;
            value = ModeCatalog.Name(selectedMode);
            bool scopeChanges = PacketEngine.IsScopedProfile(ModeCatalog.Parse(value)) ||
                PacketEngine.IsScopedProfile(ModeCatalog.Parse(_selectedDiagnosticModeText));
            if (SetProperty(ref _selectedDiagnosticModeText, value))
            {
                if (_unavailableSavedMode != null)
                    SettingsStatus = "Efeito Outbound selecionado. O arquivo antigo só será alterado ao Salvar.";
                _unavailableSavedMode = null;
                OnPropertyChanged(nameof(SelectedOutboundEffect));
                OnPropertyChanged(nameof(EffectSupportText));
                RefreshEffectRating();
                CancelCurrentPulse();
                ExperimentalOptIn = false;
                GlobalPacketSizeConsent = false;
                OnPropertyChanged(nameof(IsGlobalPacketSizeSelected));
                OnPropertyChanged(nameof(IsExperimentalSelected));
                if (scopeChanges)
                {
                    _packetEngine?.Stop();
                    _packetEngine?.ConfigureScopedProfile(ModeCatalog.Parse(value));
                }
                _startError = null;
                RefreshNetworkDiagnostics();
                OnPropertyChanged(nameof(IsBurstLossVisible));
                OnPropertyChanged(nameof(IsAsymmetricLossVisible));
                OnPropertyChanged(nameof(IsDiagnosticModeSelected));
                OnPropertyChanged(nameof(IsLegacyModeSelected));
                OnPropertyChanged(nameof(IsGlobalDropVisible));
                OnPropertyChanged(nameof(IsGlobalDelayVisible));
            }
        }
    }
    public bool IsBurstLossVisible => _selectedDiagnosticModeText == "Burst Loss";
    public bool IsAsymmetricLossVisible => _selectedDiagnosticModeText == "Asymmetric Loss";
    public bool IsDiagnosticModeSelected => IsLegacyGlobalSelected || _selectedDiagnosticModeText != "Desativado";
    public bool IsLegacyModeSelected => !IsDiagnosticModeSelected && !IsLegacyGlobalSelected;
    public bool IsGlobalDelayVisible => _selectedDiagnosticModeText == "Atraso (ms)";
    private int _globalDelayMs = 150;
    public int GlobalDelayMs
    {
        get => _globalDelayMs;
        set
        {
            int clamped = Math.Clamp(value, 0, 3000);
            if (SetProperty(ref _globalDelayMs, clamped))
            {
                if (_packetEngine != null) _packetEngine.GlobalDelayMs = clamped;
                OnPropertyChanged(nameof(NetworkDiagnosticsText));
            }
        }
    }

    public string EffectSupportText => ModeCatalog.Parse(_selectedDiagnosticModeText) switch
    {
        DiagnosticMode.GilbertElliottBurstLoss => "Perda intencional em rajadas (IPv4 TCP/UDP). TCP pode retransmitir. Somente pulso explícito, até 800 ms; IPv6/ICMP passam sem efeito.",
        DiagnosticMode.GapBasedReordering => "Reordena somente UDP IPv4 no mesmo fluxo, com gap de 1 pacote e TTL de 24 ms. TCP, IPv6 e ICMP passam sem efeito.",
        var mode when OutboundPulseScheduler.Handles(mode) => "Pacing/jitter IPv4 TCP/UDP, limitado ao pulso. IPv6, ICMP e fragmentos passam sem efeito; STOP libera pendências.",
        _ => "START apenas arma; Espaço ou Pulso manual aplica o efeito. STOP libera pendências."
    };

    // One pulse duration shared by the twelve visible effects.
    public IReadOnlyList<string> PulseStyleOptions { get; } = EffectExperienceCatalog.PrimaryStyles;
    public ICommand SelectPulseStyleCommand { get; }
    public bool IsLegitStyle { get => _selectedPulseStyle == "Legit"; set { if (value) SelectedPulseStyle = "Legit"; } }
    public bool IsAggressiveStyle { get => _selectedPulseStyle == "Agressivo"; set { if (value) SelectedPulseStyle = "Agressivo"; } }
    public bool IsManualDurationEnabled
    {
        get => _selectedPulseStyle == "Teste manual";
        set => SelectedPulseStyle = value ? "Teste manual" : "Legit";
    }
    private void RefreshPulseStyle()
    {
        OnPropertyChanged(nameof(IsLegitStyle)); OnPropertyChanged(nameof(IsAggressiveStyle));
        OnPropertyChanged(nameof(IsManualDurationEnabled)); OnPropertyChanged(nameof(PulseStyleHelpText));
    }
    private string _selectedPulseStyle = EffectExperienceCatalog.StyleName(PulseStyle.Custom);
    public string SelectedPulseStyle
    {
        get => _selectedPulseStyle;
        set
        {
            if (!EffectExperienceCatalog.Styles.TryGetValue(value ?? "", out var style) || !SetProperty(ref _selectedPulseStyle, value!)) return;
            if (EffectExperienceCatalog.FixedDuration(style) is int duration) DiagnosticCycleDurationMs = duration;
            RefreshPulseStyle();
        }
    }
    public string PulseStyleHelpText => EffectExperienceCatalog.Styles[_selectedPulseStyle] switch
    {
        PulseStyle.Legitimate => "250 ms no próximo pulso. Avaliação é pessoal, não garantia em jogo.",
        PulseStyle.Aggressive => "800 ms no próximo pulso. Avaliação é pessoal, não garantia em jogo.",
        _ => "Teste manual: ajuste só para o próximo pulso. Equilibrado é uma avaliação, não um preset."
    };
    private EffectRating CurrentEffectRating => _unavailableSavedMode != null
        ? EffectExperienceCatalog.Rating(_unavailableSavedMode.Value, DiagnosticCycleDurationMs)
        : EffectExperienceCatalog.Rating(ModeCatalog.Parse(_selectedDiagnosticModeText), DiagnosticCycleDurationMs);
    public string EffectRatingText => CurrentEffectRating.Label;
    public string EffectRatingBackground => CurrentEffectRating.Background;
    public string EffectRatingForeground => CurrentEffectRating.Foreground;
    public string EffectRatingNote => $"Avaliação pessoal · {DiagnosticCycleDurationMs} ms. {CurrentEffectRating.Note}";
    private void RefreshEffectRating()
    {
        OnPropertyChanged(nameof(EffectRatingText)); OnPropertyChanged(nameof(EffectRatingBackground));
        OnPropertyChanged(nameof(EffectRatingForeground)); OnPropertyChanged(nameof(EffectRatingNote));
    }
    private int _diagnosticCycleDurationMs = 200;
    public int DiagnosticCycleDurationMs
    {
        get => _diagnosticCycleDurationMs;
        set
        {
            int duration = Math.Clamp(value, 0, 800);
            // A fixed preset cannot be silently overridden by editing a field.
            if (EffectExperienceCatalog.FixedDuration(EffectExperienceCatalog.Styles[_selectedPulseStyle]) is int fixedMs && duration != fixedMs) return;
            if (!SetProperty(ref _diagnosticCycleDurationMs, duration)) return;
            RefreshEffectRating();
        }
    }

    private int _burstLossPercentage = 50;
    public int BurstLossPercentage
    {
        get => _burstLossPercentage;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _burstLossPercentage, clamped) && _packetEngine != null)
            {
                _packetEngine.BurstLossPercentage = clamped;
            }
        }
    }

    private int _asymmetricInboundLossPercentage = 25;
    public int AsymmetricInboundLossPercentage
    {
        get => _asymmetricInboundLossPercentage;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _asymmetricInboundLossPercentage, clamped) && _packetEngine != null)
            {
                _packetEngine.AsymmetricInboundLossPercentage = clamped;
            }
        }
    }

    private int _asymmetricOutboundLossPercentage = 75;
    public int AsymmetricOutboundLossPercentage
    {
        get => _asymmetricOutboundLossPercentage;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _asymmetricOutboundLossPercentage, clamped) && _packetEngine != null)
            {
                _packetEngine.AsymmetricOutboundLossPercentage = clamped;
            }
        }
    }

    private bool _onlyTargetForeground = true;
    public bool OnlyTargetForeground
    {
        get => _onlyTargetForeground;
        set
        {
            if (_outboundOnly) value = false;
            if (SetProperty(ref _onlyTargetForeground, value))
            {
                if (value && SelectedProcess != null)
                {
                    _hotkeyService.SetTargetWindow(SelectedProcess.MainWindowHandle);
                }
                else
                {
                    _hotkeyService.SetTargetWindow(IntPtr.Zero);
                }
            }
        }
    }

    private DesyncStateMachine.State _state = DesyncStateMachine.State.Idle;
    public DesyncStateMachine.State State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColorBrush));
                OnPropertyChanged(nameof(IsActive));
            }
        }
    }

    public string StatusText => _packetEngine is { IsCaptureRequested: true, IsRunning: false } ? "AGUARDANDO" : State switch
    {
        DesyncStateMachine.State.Idle => "IDLE",
        DesyncStateMachine.State.Armed => "ARMADO",
        DesyncStateMachine.State.Active => "DESYNC ATIVO",
        DesyncStateMachine.State.Restoring => "RESTAURANDO",
        _ => "DESCONHECIDO"
    };

    public string StatusColorBrush => _packetEngine is { IsCaptureRequested: true, IsRunning: false } ? "#FF9F1C" : State switch
    {
        DesyncStateMachine.State.Idle => "#8D93AA",       // Neutral stopped state
        DesyncStateMachine.State.Armed => "#06D6A0",
        DesyncStateMachine.State.Active => "#EF233C",     // Vivid Red
        DesyncStateMachine.State.Restoring => "#FF9F1C",  // Warning Orange
        _ => "#6C757D"
    };

    public bool IsActive => State == DesyncStateMachine.State.Active;

    private int _remainingMs = 0;
    public int RemainingMs
    {
        get => _remainingMs;
        private set
        {
            if (SetProperty(ref _remainingMs, value))
            {
                OnPropertyChanged(nameof(CountdownDisplay));
            }
        }
    }

    private int _totalMs = 800;
    public int TotalMs
    {
        get => _totalMs;
        private set => SetProperty(ref _totalMs, value);
    }

    private double _countdownProgress = 0.0;
    public double CountdownProgress
    {
        get => _countdownProgress;
        set => SetProperty(ref _countdownProgress, value);
    }

    public string CountdownDisplay => State == DesyncStateMachine.State.Active
        ? $"{_remainingMs} ms"
        : "0 ms (Aguardando)";

    private bool _isEngineRunning = false;
    public bool IsEngineRunning
    {
        get => _isEngineRunning;
        private set
        {
            if (SetProperty(ref _isEngineRunning, value))
            {
                OnPropertyChanged(nameof(EngineToggleText));
                OnPropertyChanged(nameof(EngineStatusColor));
            }
        }
    }

    public string EngineToggleText => (_packetEngine?.IsCaptureRequested ?? IsEngineRunning) ? "STOP" : "START";
    public string NetworkDiagnosticsText
    {
        get
        {
            var s = _packetEngine?.ScopeStatus ?? CaptureScopeStatus.Empty;
            if (_outboundOnly) return $"Outbound · todos os aplicativos\n{(s.Running ? "Captura ARMADA" : "Captura parada")} • pulso: {DiagnosticCycleDurationMs} ms\nIntercepções: {_packetEngine?.PacketsIntercepted ?? 0} • retidos: {_packetEngine?.PacketsThrottled ?? 0} • perdas intencionais GE: {_packetEngine?.IntentionalBurstDrops ?? 0} • falhas de envio: {_packetEngine?.PacketsFailedToSend ?? 0} • liberados scheduler: {_packetEngine?.ScheduledPacketsReleased ?? 0} • pares reordenados: {_packetEngine?.IntentionallyReorderedPairs ?? 0} • taxa alvo: {_packetEngine?.AdaptiveTargetBytesPerSecond ?? 0:F0} B/s";
            if (IsLegacyGlobalSelected) return $"GLOBAL — TODOS OS APLICATIVOS • {(s.Running ? (_packetEngine!.IsEffectActive ? "PULSO ATIVO" : "ARMADO / sem efeito") : "STOP / parado")}\nEfeito selecionado: {SelectedDiagnosticModeText} • pulso: {DiagnosticCycleDurationMs} ms" +
                (IsGlobalDelayVisible ? $" • retenção por pacote: {GlobalDelayMs} ms" : "") +
                $"\n{s.Reason}\n{_packetEngine?.ProtocolCounters}";
            return $"PID {s.ProcessId} • FLOW {(s.Observing ? "armado" : "inativo")} • NETWORK {(s.Running ? "ativo" : s.Requested ? "aguardando tupla" : "parado")}\nTCP: observados {s.TcpObserved}, escopados {s.TcpScoped} | UDP: sockets {s.UdpBindings}, fluxos observados {s.UdpObserved}, elegíveis {s.UdpEligible}, escopados {s.UdpScoped}\n{s.Reason}\n{_packetEngine?.ProtocolCounters}";
        }
    }
    public string ScopedTuplesText => (_packetEngine?.ScopeStatus.Tuples ?? "") + "\nNETWORK: " + (_packetEngine?.ScopeStatus.Filter ?? "false");

    private string? _startError;
    private string _startStatusText = "Captura parada. START arma Outbound para todos os aplicativos.";
    public string StartStatusText { get => _startStatusText; private set => SetProperty(ref _startStatusText, value); }
    public string CompactStartErrorText
    {
        get
        {
            if (CompatibilityError != null) return "Efeito salvo indisponível. Escolha um dos 12 efeitos.";
            if (_startError == null || Stopwatch.GetElapsedTime(_startErrorAt).TotalSeconds > 15) return "";
            var native = System.Text.RegularExpressions.Regex.Match(_packetEngine?.ScopeStatus.Reason ?? "", @"Win32\s+(\d+)");
            return native.Success ? native.Groups[1].Value switch
            {
                "2" => "Win32 2 · Driver não encontrado. Extraia DLL e SYS junto do EXE.",
                "5" => "Win32 5 · Acesso negado. Execute como administrador.",
                "87" => "Win32 87 · Não foi possível abrir a captura. Reinicie o aplicativo.",
                "577" => "Win32 577 · Assinatura do driver recusada. Não desative proteções.",
                _ => "START recusado. Confira a instalação e as permissões."
            } : "START recusado. Confira a instalação e as permissões.";
        }
    }
    private long _startErrorAt;
    public string CompactStateText => _packetEngine?.IsRunning == true ? (_packetEngine.IsEffectActive ? "PULSO ATIVO" : "ARMADO") : _packetEngine?.IsCaptureRequested == true ? "AGUARDANDO" : "PARADO";
    public string CaptureStateText => _packetEngine?.IsRunning == true
        ? (_packetEngine.IsEffectActive ? "PULSO ATIVO" : "ARMADO / ATIVO")
        : _packetEngine?.IsCaptureRequested == true ? "AGUARDANDO" : "PARADO / PAUSADO";

    private string? CompatibilityError => _outboundOnly
        ? (_unavailableSavedMode != null || !ModeCatalog.OutboundChoices.Values.Contains(ModeCatalog.Parse(SelectedDiagnosticModeText))
            ? "Efeito salvo INCOMPATÍVEL com Outbound puro. Selecione um efeito disponível; a configuração antiga foi preservada." : null)
        : PacketEngine.ProfileCompatibilityError(ModeCatalog.Parse(SelectedDiagnosticModeText), GetFilterMode());
    public bool IsGlobalPacketSizeSelected => IsLegacyGlobalSelected && ModeCatalog.Parse(SelectedDiagnosticModeText) == DiagnosticMode.PacketSizeAwareShaping;
    private bool _globalPacketSizeConsent;
    public bool GlobalPacketSizeConsent
    {
        get => _globalPacketSizeConsent;
        set
        {
            if (!SetProperty(ref _globalPacketSizeConsent, value)) return;
            CancelCurrentPulse();
            if (_packetEngine != null) _packetEngine.GlobalPacketSizeConsent = value;
            RefreshNetworkDiagnostics();
        }
    }

    private void RefreshNetworkDiagnostics()
    {
        if (_packetEngine == null) return;
        IsEngineRunning = _packetEngine.IsRunning;
        State = _controller.CurrentState;
        OnPropertyChanged(nameof(EngineToggleText));
        OnPropertyChanged(nameof(NetworkDiagnosticsText));
        OnPropertyChanged(nameof(ScopedTuplesText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CaptureStateText));
        OnPropertyChanged(nameof(CompactStartErrorText));
        OnPropertyChanged(nameof(CompactStateText));
        OnPropertyChanged(nameof(StatusColorBrush));
        var s = _packetEngine.ScopeStatus;
        if (_outboundOnly)
        {
            FilterStatusText = "Outbound · todos os aplicativos";
            StartStatusText = _startError ?? CompatibilityError ??
                (IsGlobalPacketSizeSelected && !GlobalPacketSizeConsent
                    ? "Consentimento obrigatório para Packet Size-Aware em todos os aplicativos: afeta apenas datagramas IPv4 de saída; os demais tráfegos passam sem efeito."
                    : s.Running ? (_packetEngine.IsEffectActive ? "Pulso ativo. " : "Captura ARMADA, sem efeito. Espaço ou Pulso manual dispara o efeito. ") + "Outbound · todos os aplicativos."
                    : "Captura parada. START arma Outbound para todos os aplicativos, sem aplicar efeito; STOP encerra e libera pacotes retidos.");
            if (!CycleProfileEnabled) StartStatusText += " Marque Habilitado para permitir o pulso; START somente arma.";
            if (_startError != null) StartStatusText += " Confira os arquivos extraídos junto do EXE e a execução como Administrador. Não desative proteções do sistema.";
            return;
        }
        FilterStatusText = IsLegacyGlobalSelected ? "Outbound global" : $"PID {s.ProcessId} · TCP {s.TcpScoped} · UDP {s.UdpScoped}";
        StartStatusText = _startError ?? CompatibilityError ??
            (IsGlobalPacketSizeSelected && !GlobalPacketSizeConsent
                ? "Consentimento GLOBAL obrigatório: UDP IPv4 de TODOS os aplicativos será afetado. TCP passa sem efeito. Marque o consentimento ou selecione Saída do alvo com PID estrito."
            : !IsLegacyGlobalSelected && SelectedProcess is not { ProcessId: > 0 }
                ? "Selecione a aplicação-alvo. Este perfil exige PID estrito e conexões verificadas, sem trocar o filtro escolhido. Depois pressione START."
            : IsExperimentalSelected && !ExperimentalOptIn
                ? "Ative o Opt-in EXPERIMENTAL para reordenar somente UDP loopback próprio. Depois pressione START."
            : s.Requested && !s.Running
                ? "Aguardando conexões do processo; reconecte para observar UDP. Captura ainda inativa; STOP cancela a espera. " + s.Reason
            : s.Running ? (_packetEngine.IsEffectActive ? "Pulso ativo. " : "Captura ARMADA, sem efeito. Espaço ou Pulso manual dispara o efeito. ") +
                (IsLegacyGlobalSelected ? "GLOBAL outbound." : $"PID {s.ProcessId} · TCP {s.TcpScoped} · UDP {s.UdpScoped}. " + (s.UdpScoped == 0 ? "Nenhum UDP incluído; reconecte para observar UDP." : ""))
            : s.ProcessId != 0 ? "Captura parada. Pressione START. " + s.Reason
            : IsLegacyGlobalSelected ? "GLOBAL outbound parado. START arma sem efeito; afeta todos os aplicativos no pulso."
            : "Selecione a aplicação-alvo e pressione START; captura exige PID vivo e conexões verificadas.");
        if (!CycleProfileEnabled) StartStatusText += " Efeito desabilitado: marque Habilitado para permitir o pulso; START somente arma.";
    }
    public string EngineStatusColor => IsEngineRunning ? "#06D6A0" : "#E63946";

    private long _packetsIntercepted = 0;
    public long PacketsIntercepted
    {
        get => _packetsIntercepted;
        set => SetProperty(ref _packetsIntercepted, value);
    }

    private long _packetsDropped = 0;
    public long PacketsDropped
    {
        get => _packetsDropped;
        set => SetProperty(ref _packetsDropped, value);
    }

    private long _packetsPassed = 0;
    public long PacketsPassed
    {
        get => _packetsPassed;
        set => SetProperty(ref _packetsPassed, value);
    }

    private long _packetsThrottled = 0;
    public long PacketsThrottled
    {
        get => _packetsThrottled;
        set => SetProperty(ref _packetsThrottled, value);
    }

    private string _filterStatusText = "Captura parada • filtro preparado: outbound";
    public string FilterStatusText
    {
        get => _filterStatusText;
        private set => SetProperty(ref _filterStatusText, value);
    }

    // Commands
    public ICommand TriggerCycleCommand { get; }
    public ICommand CancelCycleCommand { get; }
    public ICommand ToggleEngineCommand { get; }
    public ICommand CopyDiagnosticCommand { get; }
    private string _lastStartDiagnostic = "Nenhum START pressionado nesta sessão.";

    // Allowlist only: never copy process identity, tuples, custom filter, exception
    // text, file paths, tokens or raw logs. Native codes survive without their text.
    public string SupportDiagnosticText => "DarkDesync — diagnóstico redigido\n" +
        "Último START:\n" + _lastStartDiagnostic + "\nEstado atual:\n" + SafeDiagnosticSnapshot();

    private string SafeDiagnosticSnapshot()
    {
        var s = _packetEngine?.ScopeStatus ?? CaptureScopeStatus.Empty;
        var native = System.Text.RegularExpressions.Regex.Match(s.Reason, @"Win32\s+(\d+)");
        if (_outboundOnly)
        {
            string outboundReason = CompatibilityError != null ? "Efeito salvo incompatível com Outbound puro" :
                IsGlobalPacketSizeSelected && !GlobalPacketSizeConsent ? "Consentimento Packet Size-Aware ausente" :
                native.Success ? "Falha WinDivert; consultar código Win32" :
                s.Running ? "ARMADO" : _startError != null ? "START recusado; detalhe local omitido por privacidade" : "PARADO";
            return $"scope=Outbound · todos os aplicativos; appliedFilter={_packetEngine?.ConfiguredFilterMode}; mode={_unavailableSavedMode ?? ModeCatalog.Parse(SelectedDiagnosticModeText)}; pulseMs={DiagnosticCycleDurationMs}; enabled={CycleProfileEnabled}\n" +
                $"packetSizeConsent={GlobalPacketSizeConsent}; requested={_packetEngine?.IsCaptureRequested ?? false}; running={_packetEngine?.IsRunning ?? false}; Win32={(native.Success ? native.Groups[1].Value : "none")}\n" +
                $"reason={outboundReason}; intercepted={_packetEngine?.PacketsIntercepted ?? 0}; held={_packetEngine?.PacketsThrottled ?? 0}; intentionalLoss={_packetEngine?.PacketsDropped ?? 0}";
        }
        string reason = CompatibilityError != null ? "INCOMPATÍVEL: direção/escopo do efeito" :
            IsGlobalPacketSizeSelected && !GlobalPacketSizeConsent ? "Consentimento GLOBAL UDP ausente" :
            !IsLegacyGlobalSelected && SelectedProcess is not { ProcessId: > 0 } ? "PID obrigatório ausente" :
            IsExperimentalSelected && !ExperimentalOptIn ? "Opt-in EXPERIMENTAL ausente" :
            native.Success ? "Falha WinDivert; consultar código Win32" :
            s.Running ? "ARMADO" : s.Requested ? "AGUARDANDO tupla exata" :
            _startError != null ? "START recusado; detalhe local omitido por privacidade" : "PARADO";
        return $"filter={GetFilterMode()}; mode={ModeCatalog.Parse(SelectedDiagnosticModeText)}; pulseMs={DiagnosticCycleDurationMs}; enabled={CycleProfileEnabled}\n" +
            $"appliedFilter={_packetEngine?.ConfiguredFilterMode}; armedScopeProfile={_packetEngine?.ArmedScopeProfile}; globalUdpConsent={GlobalPacketSizeConsent}; experimental={ExperimentalOptIn}; foregroundOnly={OnlyTargetForeground}; hotkey={(AvailableHotkeys.ContainsKey(SelectedHotkeyName) ? SelectedHotkeyName : "unknown")}; targetSelected={SelectedProcess is { ProcessId: > 0 }}\n" +
            $"requested={_packetEngine?.IsCaptureRequested ?? false}; running={_packetEngine?.IsRunning ?? false}; FLOW={s.Observing}; Win32={(native.Success ? native.Groups[1].Value : "none")}\n" +
            $"reason={reason}; TCP observed/scoped={s.TcpObserved}/{s.TcpScoped}; UDP bindings/observed/eligible/scoped={s.UdpBindings}/{s.UdpObserved}/{s.UdpEligible}/{s.UdpScoped}";
    }

    private void CopyDiagnostic()
    {
        try
        {
            Clipboard.SetText(SupportDiagnosticText);
            AddLog("[SUPORTE] Diagnóstico copiado por clique; sem IP/portas, PID, nomes de processo ou logs brutos.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        { AddLog("[SUPORTE] Área de transferência indisponível; tente Copiar diagnóstico novamente."); }
    }
    public ICommand RefreshProcessesCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand SelectProfileCommand { get; }

    public MainViewModel(
        ProcessService processService,
        HotkeyService hotkeyService,
        DesyncController controller,
        PacketEngine? packetEngine = null,
        SettingsStore? settingsStore = null)
        : this(processService, hotkeyService, controller, packetEngine, settingsStore, outboundOnly: true) { }

    // Only the regression harness can select legacy strict control-plane behavior.
    // MainWindow always uses the public outbound-only constructor.
    internal MainViewModel(ProcessService processService, HotkeyService hotkeyService,
        DesyncController controller, PacketEngine? packetEngine, SettingsStore? settingsStore, bool outboundOnly)
    {
        _outboundOnly = outboundOnly;
        DiagnosticModeOptions = (outboundOnly ? ModeCatalog.OutboundChoices : ModeCatalog.Choices).Keys.ToList();
        _processService = processService;
        _hotkeyService = hotkeyService;
        _controller = controller;
        _packetEngine = packetEngine;
        _settingsStore = settingsStore ?? new SettingsStore();
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        AvailableHotkeys = HotkeyService.GetAvailableKeys();

        // Initialize Profiles
        foreach (var p in IntensityProfile.Defaults)
        {
            Profiles.Add(p);
        }
        _selectedProfile = Profiles[0];
        _controller.ActiveProfile = IntensityProfile.Weak;
        _customDurationMs = _selectedProfile.DurationMs;
        _dropPercentage = _selectedProfile.DropPercentage;

        // Commands
        TriggerCycleCommand = new RelayCommand(ExecuteTriggerCycle);
        SelectPulseStyleCommand = new RelayCommand(p => SelectedPulseStyle = p as string ?? "Teste manual");
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        LoadSettingsCommand = new RelayCommand(LoadSettings);
        ResetSettingsCommand = new RelayCommand(() => ApplySettings(new AppSettings()));
        CancelCycleCommand = new RelayCommand(ExecuteCancelCycle);
        ToggleEngineCommand = new RelayCommand(ExecuteToggleEngine);
        CopyDiagnosticCommand = new RelayCommand(CopyDiagnostic);
        RefreshProcessesCommand = new RelayCommand(RefreshProcessList);
        ClearHistoryCommand = new RelayCommand(() => History.Clear());
        SelectProfileCommand = new RelayCommand(p =>
        {
            if (p is IntensityProfile prof)
            {
                SelectedProfile = prof;
                CustomDurationMs = prof.DurationMs;
                DropPercentage = prof.DropPercentage;
            }
        });

        // Wire Controller events
        _controller.StateChanged += _ => _dispatcher.InvokeAsync(() => State = _controller.CurrentState);
        // Countdown remains inside the controller; the controls-only UI does not
        // subscribe or dispatch per-tick visual updates.
        _controller.CycleFinished += record => _dispatcher.InvokeAsync(() =>
        {
            History.Insert(0, record);
            AddLog($"[CICLO] Finalizado: {record.Summary}");
        });
        _controller.LogMessage += msg => _dispatcher.InvokeAsync(() => AddLog(msg));

        // Wire PacketEngine events if present
        if (_packetEngine != null)
        {
            // Pull telemetry on the UI timer only. Never queue UI work per packet.
            _packetEngine.ErrorOccurred += err => _dispatcher.InvokeAsync(() =>
            {
                AddLog($"[ERRO MOTOR] {err}");
                if (!_packetEngine.IsCaptureRequested && _packetEngine.ScopeStatus.Reason.Contains(err)) { _startError = err; _startErrorAt = Stopwatch.GetTimestamp(); }
                RefreshNetworkDiagnostics();
                IsEngineRunning = _packetEngine.IsRunning;
            });
        }

        // Wire Hotkey
        _hotkeyService.HotkeyPressed += () =>
        {
            // Snapshot acceptance at the physical edge, not after a queued START.
            // No packet locks or native work on the low-level hook thread.
            if (_packetEngine is not { IsCaptureRequested: true, IsRunning: true } ||
                _packetEngine.StateMachine.Current != DesyncStateMachine.State.Armed) return;
            long epoch = _packetEngine.ArmedGeneration;
            _dispatcher.InvokeAsync(() =>
            {
                if (_packetEngine.ArmedGeneration != epoch) return;
                AddLog("[HOTKEY] Tecla acionada via teclado");
                ExecuteTriggerCycle("atalho global");
            });
        };

        // Low-priority telemetry, independent of packet rate (4 updates/second).
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _statsTimer.Tick += (s, e) =>
        {
            // Avoid rendering telemetry while minimized; refresh on the next visible tick.
            if (Application.Current?.MainWindow?.WindowState == WindowState.Minimized)
                return;

            if (_packetEngine != null)
            {
                PacketsIntercepted = _packetEngine.PacketsIntercepted;
                PacketsDropped = _packetEngine.PacketsDropped;
                PacketsPassed = _packetEngine.PacketsPassedThrough;
                PacketsThrottled = _packetEngine.PacketsThrottled;
                RefreshNetworkDiagnostics();
            }
        };
        _statsTimer.Start(); // One low-priority snapshot poll, never dispatch per packet.

        // Populate initial process list
        if (_outboundOnly)
        {
            _selectedFilterModeText = FilterModeOptions[4];
            _onlyTargetForeground = false;
            SettingsStatus = "Outbound afeta TODOS os aplicativos. START só arma; Espaço aplica o pulso. Salvar é explícito.";
            ApplyFilterMode();
        }
        else RefreshProcessList();

        // Only START opens capture; hotkey/manual pulse requires an armed engine.

        AddLog("[SISTEMA] DarkDesync inicializado com sucesso.");
    }

    public void EnsureEngineRunning(bool startCapture = true)
    {
        if (startCapture)
        {
            _startError = null;
            if (CompatibilityError != null || (IsGlobalPacketSizeSelected && !GlobalPacketSizeConsent) ||
                (!IsLegacyGlobalSelected && SelectedProcess is not { ProcessId: > 0 }) ||
                (IsExperimentalSelected && !ExperimentalOptIn))
            {
                RefreshNetworkDiagnostics();
                AddLog("[PREFLIGHT] " + StartStatusText);
                return;
            }
        }
        if (_packetEngine != null)
        {
            _packetEngine.ThrottleEnabled = ThrottleEnabled;
            _packetEngine.CycleDropEnabled = CycleProfileEnabled;
            _packetEngine.ThrottleContinuous = SelectedThrottleMode == ThrottleModeOptions[1];
            _packetEngine.ThrottleTimeframeMs = ThrottleTimeframeMs;
            _packetEngine.ThrottleChance = ThrottleChance;
            _packetEngine.ThrottleReleaseDelayMs = ThrottleReleaseDelayMs;
            _packetEngine.ThrottleJitterMs = ThrottleJitterMs;
            _packetEngine.BurstLossPercentage = BurstLossPercentage;
            _packetEngine.GlobalDelayMs = GlobalDelayMs;
            _packetEngine.AsymmetricInboundLossPercentage = AsymmetricInboundLossPercentage;
            _packetEngine.AsymmetricOutboundLossPercentage = AsymmetricOutboundLossPercentage;

            if (startCapture && !_packetEngine.IsRunning && !IsLegacyGlobalSelected)
            {
                var mode = GetFilterMode();
                uint pid = SelectedProcess?.ProcessId ?? 0;
                _packetEngine.ConfigureScopedProfile(ModeCatalog.Parse(SelectedDiagnosticModeText));
                bool ok = _packetEngine.StartArmed(mode, pid, DropPercentage, CustomFilterText);
                if (ok)
                {
                    IsEngineRunning = true;
                    FilterStatusText = $"Filtro: {_packetEngine.CurrentFilter}";
                    AddLog($"[MOTOR] Captura WinDivert iniciada por ação explícita ({_packetEngine.CurrentFilter}).");
                }
                else if (!_packetEngine.IsCaptureRequested) _startError = "Não foi possível iniciar: " + _packetEngine.ScopeStatus.Reason;
                RefreshNetworkDiagnostics();
                if (!ok) AddLog("[PREFLIGHT] " + StartStatusText);
            }
        }
    }

    private FilterMode GetFilterMode()
    {
        if (_outboundOnly) return FilterMode.LegacyGlobalOutbound;
        return SelectedFilterModeText switch
        {
            "Saída do alvo (IPv4, TCP/UDP)" => FilterMode.Outbound,
            "Entrada + saída do alvo (IPv4, TCP/UDP)" => FilterMode.AllTraffic,
            "Conexões verificadas da aplicação" => FilterMode.ProcessPorts,
            "Personalizado" => FilterMode.Custom,
            "Outbound legado — todos os aplicativos (global)" => FilterMode.LegacyGlobalOutbound,
            _ => FilterMode.Outbound
        };
    }

    private void ApplyFilterMode()
    {
        _startError = null;
        OnPropertyChanged(nameof(IsCustomFilterVisible));
        var mode = GetFilterMode();
        uint pid = SelectedProcess?.ProcessId ?? 0;

        CancelCurrentPulse();
        _packetEngine?.Stop(); // Target/filter changes require a new START.
        _packetEngine?.UpdateFilter(mode, pid, CustomFilterText); // Arms observation on selection, before joining a service.
        RefreshNetworkDiagnostics();
    }

    private void ExecuteTriggerCycle() => ExecuteTriggerCycle("pulso manual");

    private void ExecuteTriggerCycle(string source)
    {
        if (_packetEngine is not { IsCaptureRequested: true, IsRunning: true })
        {
            RefreshNetworkDiagnostics();
            AddLog("[AVISO] Pressione START antes do Espaço/pulso manual. O atalho não inicia captura.");
            return;
        }
        if (!CycleProfileEnabled || (IsExperimentalSelected && !ExperimentalOptIn))
        { RefreshNetworkDiagnostics(); return; } // Hidden legacy settings never select an effect.

        var diagMode = ModeCatalog.Parse(_selectedDiagnosticModeText);

        AddLog($"[ACIONAMENTO] {source}; escopo: {(IsLegacyGlobalSelected ? "GLOBAL" : "PID estrito")}; efeito: {diagMode}; duração de pulso configurada: {DiagnosticCycleDurationMs} ms");
        int duration = DiagnosticCycleDurationMs;
        bool cycleStarted = _packetEngine.TriggerArmedEffect(diagMode, duration);
        if (!cycleStarted) AddLog("[AVISO] Não foi possível iniciar ciclo.");
        RefreshNetworkDiagnostics();
    }

    private void CancelCurrentPulse()
    {
        if (_packetEngine?.IsCaptureRequested == true) _packetEngine.CancelArmedEffect();
        else _controller.CancelCycle();
    }

    private void ExecuteCancelCycle()
    {
        CancelCurrentPulse();
        if (_packetEngine is { IsRunning: false }) _packetEngine.Stop(); // Also stop pending UDP observation.
        _startError = null;
        RefreshNetworkDiagnostics();
        AddLog("[SISTEMA] Ciclo cancelado pelo usuário.");
    }

    private void ExecuteToggleEngine()
    {
        bool stopping = _packetEngine?.IsCaptureRequested ?? IsEngineRunning;
        AddLog(stopping ? "[STOP PRESSED] Cancelando captura/espera." : "[START PRESSED] " + SafeDiagnosticSnapshot());
        try { ExecuteToggleEngineCore(); }
        finally
        {
            RefreshNetworkDiagnostics();
            if (!stopping) _lastStartDiagnostic = SafeDiagnosticSnapshot();
            AddLog("[" + (stopping ? "STOP" : "START") + " RESULT] " + StartStatusText);
        }
    }

    private void ExecuteToggleEngineCore()
    {
        _startError = null;
        if (IsLegacyGlobalSelected)
        {
            if (_packetEngine == null) return;
            if (_packetEngine.IsRunning || _packetEngine.IsCaptureRequested)
            {
                _packetEngine.Stop();
                CancelCurrentPulse();
                AddLog("[GLOBAL] STOP: captura encerrada e pacotes retidos liberados.");
            }
            else
            {
                CancelCurrentPulse();
                EnsureEngineRunning(startCapture: false);
                if (CompatibilityError != null || (IsGlobalPacketSizeSelected && !GlobalPacketSizeConsent))
                {
                    RefreshNetworkDiagnostics();
                    AddLog("[BLOQUEADO] " + StartStatusText);
                    return;
                }
                _packetEngine.ConfigureScopedProfile(ModeCatalog.Parse(SelectedDiagnosticModeText));
                _packetEngine.SetDropPercentage(DropPercentage);
                bool started = _packetEngine.BeginLegacyGlobalContinuous(DiagnosticMode.None);
                // ErrorOccurred is dispatched asynchronously. Preserve the refusal
                // synchronously too, otherwise START RESULT falsely looks stopped/ready.
                if (!started) { _startError = _packetEngine.ScopeStatus.Reason; _startErrorAt = Stopwatch.GetTimestamp(); }
                AddLog(started
                    ? "[GLOBAL] START: filtro outbound ARMADO, SEM efeito. Espaço dispara um pulso; STOP fecha a captura."
                    : "[GLOBAL] Não foi possível iniciar. Verifique driver/administrador e diagnóstico.");
            }
            RefreshNetworkDiagnostics();
            return;
        }
        if (_packetEngine?.IsCaptureRequested ?? IsEngineRunning)
        {
            CancelCurrentPulse();
            _packetEngine?.Stop();
            IsEngineRunning = false;
            FilterStatusText = "Filtro: Desativado";
            AddLog("[MOTOR] Interceptação parada.");
        }
        else
        {
            EnsureEngineRunning();
        }
        RefreshNetworkDiagnostics();
    }

    public void LoadSettings()
    {
        try
        {
            var settings = _settingsStore.Load();
            ApplySettings(settings);
            bool migrated = !ModeCatalog.Choices.Values.Contains(settings.DiagnosticMode);
            SettingsStatus = _outboundOnly
                ? (_unavailableSavedMode != null
                    ? $"Efeito salvo {_unavailableSavedMode.Value} indisponível em Outbound puro. Escolha um efeito disponível. Arquivo original intacto."
                    : "Configuração carregada sem alterar o arquivo. Interface Outbound · todos os aplicativos; captura parada.")
                : migrated
                ? "Efeito antigo → Outbound Freeze. Arquivo original mantido até Salvar."
                : "Configuração carregada · captura parada.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { SettingsStatus = $"Não foi possível carregar: {ex.Message}"; }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(new AppSettings
            {
                CycleProfileEnabled = CycleProfileEnabled, ThrottleEnabled = ThrottleEnabled,
                ThrottleMode = SelectedThrottleMode == ThrottleModeOptions[1] ? ThrottleMode.Continuous : ThrottleMode.Hotkey,
                DurationMs = CustomDurationMs, DropPercentage = DropPercentage, RetentionMs = ThrottleTimeframeMs,
                Chance = ThrottleChance, SpacingMs = ThrottleReleaseDelayMs, JitterMs = ThrottleJitterMs,
                Hotkey = SelectedHotkeyName, OnlyTargetForeground = OnlyTargetForeground,
                FilterMode = GetFilterMode(), CustomFilter = CustomFilterText,
                DiagnosticDurationMs = DiagnosticCycleDurationMs, GlobalDelayMs = GlobalDelayMs,
                PulseStyle = EffectExperienceCatalog.Styles[_selectedPulseStyle],
                BurstLossPercentage = BurstLossPercentage,
                AsymmetricInboundPercentage = AsymmetricInboundLossPercentage,
                AsymmetricOutboundPercentage = AsymmetricOutboundLossPercentage,
                DiagnosticMode = _unavailableSavedMode ?? ModeCatalog.Parse(_selectedDiagnosticModeText)
            });
            SettingsStatus = _outboundOnly ? "Configuração salva. Captura Outbound só inicia por START." : "Configuração salva. O alvo deve ser selecionado novamente.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { SettingsStatus = $"Não foi possível salvar: {ex.Message}"; }
    }

    private void ApplySettings(AppSettings settings)
    {
        var s = settings.Validate();
        CancelCurrentPulse();
        ExperimentalOptIn = false;
        GlobalPacketSizeConsent = false;
        _packetEngine?.Stop();
        IsEngineRunning = false;
        CycleProfileEnabled = s.CycleProfileEnabled;
        SelectedThrottleMode = ThrottleModeOptions[s.ThrottleMode == ThrottleMode.Continuous ? 1 : 0];
        ThrottleEnabled = s.ThrottleEnabled; CustomDurationMs = s.DurationMs; DropPercentage = s.DropPercentage;
        ThrottleTimeframeMs = s.RetentionMs; ThrottleChance = s.Chance; ThrottleReleaseDelayMs = s.SpacingMs;
        ThrottleJitterMs = s.JitterMs; SelectedHotkeyName = s.Hotkey; OnlyTargetForeground = s.OnlyTargetForeground;
        _selectedPulseStyle = EffectExperienceCatalog.StyleName(s.PulseStyle);
        DiagnosticCycleDurationMs = s.DiagnosticDurationMs;
        OnPropertyChanged(nameof(SelectedPulseStyle)); OnPropertyChanged(nameof(PulseStyleHelpText));
        RefreshPulseStyle();
        GlobalDelayMs = s.GlobalDelayMs;
        BurstLossPercentage = s.BurstLossPercentage;
        AsymmetricInboundLossPercentage = s.AsymmetricInboundPercentage;
        AsymmetricOutboundLossPercentage = s.AsymmetricOutboundPercentage;
        SelectedProcess = null;
        SelectedFilterModeText = s.FilterMode switch
        {
            FilterMode.AllTraffic => FilterModeOptions[1], FilterMode.ProcessPorts => FilterModeOptions[2],
            FilterMode.Custom => FilterModeOptions[3], FilterMode.LegacyGlobalOutbound => FilterModeOptions[4],
            _ => FilterModeOptions[0]
        };
        // Legacy fields stay serializable, but cannot activate hidden loss/throttle paths.
        // Load never writes the source file; the UI reports the safe effect migration.
        _unavailableSavedMode = _outboundOnly && s.DiagnosticMode != DiagnosticMode.None && !ModeCatalog.OutboundChoices.Values.Contains(s.DiagnosticMode)
            ? s.DiagnosticMode : null;
        if (_unavailableSavedMode != null)
        {
            _selectedDiagnosticModeText = _unavailableSavedMode.Value.ToString();
            OnPropertyChanged(nameof(SelectedDiagnosticModeText));
            OnPropertyChanged(nameof(SelectedOutboundEffect));
            OnPropertyChanged(nameof(EffectSupportText));
            OnPropertyChanged(nameof(IsGlobalPacketSizeSelected));
            RefreshEffectRating();
        }
        else SelectedDiagnosticModeText = ModeCatalog.Name(s.DiagnosticMode);
        CustomFilterText = s.CustomFilter;
        ApplyFilterMode();
        SettingsStatus = "Padrões restaurados • captura parada. Salve para manter.";
    }

    public void RefreshProcessList()
    {
        if (_outboundOnly) return;
        var list = _processService.GetWindowedProcesses();
        Processes.Clear();
        foreach (var p in list)
        {
            Processes.Add(p);
        }

        if (SelectedProcess != null)
        {
            var match = Processes.FirstOrDefault(p => p.ProcessId == SelectedProcess.ProcessId);
            if (match != null)
                SelectedProcess = match;
        }

        AddLog($"[PROCESSO] Lista atualizada: {Processes.Count} janelas encontradas.");
    }

    public void AddLog(string line)
    {
        string timestamped = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        Logs.Insert(0, timestamped);
        if (Logs.Count > 200)
            Logs.RemoveAt(Logs.Count - 1);
    }

    public void Dispose()
    {
        _statsTimer.Stop();
        _hotkeyService.Dispose();
        _controller.Dispose();
        _packetEngine?.Dispose();
    }
}
