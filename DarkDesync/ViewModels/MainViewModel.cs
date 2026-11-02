using System.Collections.ObjectModel;
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
    public string SettingsStatus { get => _settingsStatus; private set => SetProperty(ref _settingsStatus, value); }
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
            if (SetProperty(ref _selectedFilterModeText, value))
            {

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
    private string _selectedDiagnosticModeText = "Outbound Freeze";
    public List<string> DiagnosticModeOptions { get; } = new() { "Outbound Freeze", "Burst Buffering", "Slotted Transmission", "Sawtooth Delay", "Bandwidth Shaping", "Adaptive Freeze" };

    public string SelectedDiagnosticModeText
    {
        get => _selectedDiagnosticModeText;
        set
        {
            if (!DiagnosticModeOptions.Contains(value)) return;
            if (SetProperty(ref _selectedDiagnosticModeText, value))
            {
                CancelCurrentPulse();
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

    // One pulse duration shared by the six visible effects.
    private int _diagnosticCycleDurationMs = 200;
    public int DiagnosticCycleDurationMs
    {
        get => _diagnosticCycleDurationMs;
        set => SetProperty(ref _diagnosticCycleDurationMs, Math.Clamp(value, 0, 800));
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

    public string StatusText => State switch
    {
        DesyncStateMachine.State.Idle => "IDLE",
        DesyncStateMachine.State.Armed => "ARMADO",
        DesyncStateMachine.State.Active => "DESYNC ATIVO",
        DesyncStateMachine.State.Restoring => "RESTAURANDO",
        _ => "DESCONHECIDO"
    };

    public string StatusColorBrush => State switch
    {
        DesyncStateMachine.State.Idle => "#A78BFA",       // Neon / Cool Blue
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
            if (IsLegacyGlobalSelected) return $"GLOBAL — TODOS OS APLICATIVOS • {(s.Running ? (_packetEngine!.IsEffectActive ? "PULSO ATIVO" : "ARMADO / sem efeito") : "STOP / parado")}\nEfeito selecionado: {SelectedDiagnosticModeText} • pulso: {DiagnosticCycleDurationMs} ms" +
                (IsGlobalDelayVisible ? $" • retenção por pacote: {GlobalDelayMs} ms" : "") +
                $"\n{s.Reason}\n{_packetEngine?.ProtocolCounters}";
            return $"PID {s.ProcessId} • FLOW {(s.Observing ? "armado" : "inativo")} • NETWORK {(s.Running ? "ativo" : s.Requested ? "aguardando tupla" : "parado")}\nTCP: observados {s.TcpObserved}, escopados {s.TcpScoped} | UDP: sockets {s.UdpBindings}, fluxos observados {s.UdpObserved}, elegíveis {s.UdpEligible}, escopados {s.UdpScoped}\n{s.Reason}\n{_packetEngine?.ProtocolCounters}";
        }
    }
    public string ScopedTuplesText => (_packetEngine?.ScopeStatus.Tuples ?? "") + "\nNETWORK: " + (_packetEngine?.ScopeStatus.Filter ?? "false");

    private void RefreshNetworkDiagnostics()
    {
        if (_packetEngine == null) return;
        IsEngineRunning = _packetEngine.IsRunning;
        OnPropertyChanged(nameof(EngineToggleText));
        OnPropertyChanged(nameof(NetworkDiagnosticsText));
        OnPropertyChanged(nameof(ScopedTuplesText));
        var s = _packetEngine.ScopeStatus;
        FilterStatusText = IsLegacyGlobalSelected ? "Outbound global" : $"PID {s.ProcessId} · TCP {s.TcpScoped} · UDP {s.UdpScoped}";
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
    public ICommand RefreshProcessesCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand SelectProfileCommand { get; }

    public MainViewModel(
        ProcessService processService,
        HotkeyService hotkeyService,
        DesyncController controller,
        PacketEngine? packetEngine = null,
        SettingsStore? settingsStore = null)
    {
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
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        LoadSettingsCommand = new RelayCommand(LoadSettings);
        ResetSettingsCommand = new RelayCommand(() => ApplySettings(new AppSettings()));
        CancelCycleCommand = new RelayCommand(ExecuteCancelCycle);
        ToggleEngineCommand = new RelayCommand(ExecuteToggleEngine);
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
                FilterStatusText = err;
                IsEngineRunning = _packetEngine.IsRunning;
            });
        }

        // Wire Hotkey
        _hotkeyService.HotkeyPressed += () =>
        {
            // Snapshot acceptance at the physical edge, not after a queued START.
            // No packet locks or native work on the low-level hook thread.
            if (_packetEngine is not { IsCaptureRequested: true } ||
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
        RefreshProcessList();

        // Only START opens capture; hotkey/manual pulse requires an armed engine.

        AddLog("[SISTEMA] DarkDesync inicializado com sucesso.");
    }

    public void EnsureEngineRunning(bool startCapture = true)
    {
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
                bool ok = _packetEngine.StartArmed(mode, pid, DropPercentage, CustomFilterText);
                if (ok)
                {
                    IsEngineRunning = true;
                    FilterStatusText = $"Filtro: {_packetEngine.CurrentFilter}";
                    AddLog($"[MOTOR] Captura WinDivert iniciada por ação explícita ({_packetEngine.CurrentFilter}).");
                }
            }
        }
    }

    private FilterMode GetFilterMode()
    {
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
            AddLog("[AVISO] Pressione START antes do Espaço/pulso manual. O atalho não inicia captura.");
            return;
        }
        if (!CycleProfileEnabled) return; // Hidden legacy throttle/drop settings never select an effect.

        var diagMode = _selectedDiagnosticModeText switch
        {
            "Outbound Freeze" => DiagnosticMode.OutboundFreeze,
            "Burst Buffering" => DiagnosticMode.BurstBuffering,
            "Slotted Transmission" => DiagnosticMode.SlottedTransmission,
            "Sawtooth Delay" => DiagnosticMode.SawtoothDelay,
            "Bandwidth Shaping" => DiagnosticMode.BandwidthShaping,
            "Adaptive Freeze" => DiagnosticMode.AdaptiveFreeze,
            _ => DiagnosticMode.OutboundFreeze
        };

        AddLog($"[ACIONAMENTO] {source}; escopo: {(IsLegacyGlobalSelected ? "GLOBAL" : "PID estrito")}; efeito: {diagMode}; duração de pulso configurada: {DiagnosticCycleDurationMs} ms");
        int duration = DiagnosticCycleDurationMs;
        bool cycleStarted = _packetEngine.TriggerArmedEffect(diagMode, duration);
        if (!cycleStarted) AddLog("[AVISO] Não foi possível iniciar ciclo.");
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
        AddLog("[SISTEMA] Ciclo cancelado pelo usuário.");
    }

    private void ExecuteToggleEngine()
    {
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
                _packetEngine.SetDropPercentage(DropPercentage);
                bool started = _packetEngine.BeginLegacyGlobalContinuous(DiagnosticMode.None);
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
    }

    public void LoadSettings()
    {
        try
        {
            var settings = _settingsStore.Load();
            ApplySettings(settings);
            bool migrated = settings.DiagnosticMode is not (DiagnosticMode.OutboundFreeze or DiagnosticMode.BurstBuffering or DiagnosticMode.SlottedTransmission or DiagnosticMode.SawtoothDelay or DiagnosticMode.BandwidthShaping or DiagnosticMode.AdaptiveFreeze);
            SettingsStatus = migrated
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
                BurstLossPercentage = BurstLossPercentage,
                AsymmetricInboundPercentage = AsymmetricInboundLossPercentage,
                AsymmetricOutboundPercentage = AsymmetricOutboundLossPercentage,
                DiagnosticMode = _selectedDiagnosticModeText switch
                {
                    "Outbound Freeze" => DiagnosticMode.OutboundFreeze,
                    "Burst Buffering" => DiagnosticMode.BurstBuffering,
                    "Slotted Transmission" => DiagnosticMode.SlottedTransmission,
                    "Sawtooth Delay" => DiagnosticMode.SawtoothDelay,
                    "Bandwidth Shaping" => DiagnosticMode.BandwidthShaping,
                    "Adaptive Freeze" => DiagnosticMode.AdaptiveFreeze,
                    _ => DiagnosticMode.OutboundFreeze
                }
            });
            SettingsStatus = "Configuração salva. O alvo deve ser selecionado novamente.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        { SettingsStatus = $"Não foi possível salvar: {ex.Message}"; }
    }

    private void ApplySettings(AppSettings settings)
    {
        var s = settings.Validate();
        CancelCurrentPulse();
        _packetEngine?.Stop();
        IsEngineRunning = false;
        CycleProfileEnabled = s.CycleProfileEnabled;
        SelectedThrottleMode = ThrottleModeOptions[s.ThrottleMode == ThrottleMode.Continuous ? 1 : 0];
        ThrottleEnabled = s.ThrottleEnabled; CustomDurationMs = s.DurationMs; DropPercentage = s.DropPercentage;
        ThrottleTimeframeMs = s.RetentionMs; ThrottleChance = s.Chance; ThrottleReleaseDelayMs = s.SpacingMs;
        ThrottleJitterMs = s.JitterMs; SelectedHotkeyName = s.Hotkey; OnlyTargetForeground = s.OnlyTargetForeground;
        DiagnosticCycleDurationMs = s.DiagnosticDurationMs;
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
        SelectedDiagnosticModeText = s.DiagnosticMode switch
        {
            DiagnosticMode.OutboundFreeze => "Outbound Freeze",
            DiagnosticMode.BurstBuffering => "Burst Buffering",
            DiagnosticMode.SlottedTransmission => "Slotted Transmission",
            DiagnosticMode.SawtoothDelay => "Sawtooth Delay",
            DiagnosticMode.BandwidthShaping => "Bandwidth Shaping",
            DiagnosticMode.AdaptiveFreeze => "Adaptive Freeze",
            _ => "Outbound Freeze"
        };
        CustomFilterText = s.CustomFilter;
        ApplyFilterMode();
        SettingsStatus = "Padrões restaurados • captura parada. Salve para manter.";
    }

    public void RefreshProcessList()
    {
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
