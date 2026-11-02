using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DarkDesync.Core;

/// <summary>
/// Packet payload and metadata held in the throttle queue.
/// </summary>
public sealed class ThrottledPacket
{
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public uint Length { get; init; }
    public WinDivertNative.Address Address { get; init; }
    public long TimestampTicks { get; init; }
    public long ReleaseAtTicks { get; set; }
}

/// <summary>
/// Core WinDivert packet interception engine for DarkDesync.
/// Intercepts network packets at the kernel level using WinDivert 2.2.
///
/// Behaviors:
/// - Drop: Drops packets according to DropPercentage.
/// - Throttle (Clumsy style): Holds packets for a configurable Timeframe (ms),
///   then releases them in bursts or with controlled speed (release delay ms).
/// - Transparent Pass-Through: In IDLE / RESTORING states, re-injects all packets.
/// </summary>
public sealed class PacketEngine : IDisposable
{
    private readonly ILogger<PacketEngine> _logger;
    private readonly ProcessNetworkScanner _scanner;
    private readonly FilterBuilder _filterBuilder;
    private readonly FlowTracker _flowTracker;
    private readonly DesyncStateMachine _stateMachine;

    private IntPtr _networkHandle = WinDivertNative.InvalidHandle;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Task? _throttleReleaseTask;
    private sealed class CaptureEpoch(IntPtr handle)
    {
        public IntPtr Handle { get; } = handle;
        public volatile bool Retiring;
    }
    private CaptureEpoch? _epoch;
    private readonly SemaphoreSlim _scopeSignal = new(0, 1);
    private readonly CancellationTokenSource _discoveryCts = new();
    private Task? _discoveryTask;
    private volatile bool _captureRequested;
    private volatile bool _reconfiguring;
    private volatile bool _disposed;
    private CaptureScopeStatus _scopeStatus = CaptureScopeStatus.Empty;
    private string? _lastCaptureError;
    private volatile bool _legacyPulseActive;
    private long _legacyDeadlineTicks;
    private Timer? _legacyPulseTimer;
    private int _legacyGeneration;
    public bool IsLegacyGlobalPulse => _legacyPulseActive;
    private bool WithinLegacyDeadline => Stopwatch.GetTimestamp() < Interlocked.Read(ref _legacyDeadlineTicks);
    public CaptureScopeStatus ScopeStatus => Volatile.Read(ref _scopeStatus);
    public bool IsCaptureRequested => _captureRequested;
    internal (Task? Capture, Task? Release) WorkerSnapshot
    {
        get { lock (_lifecycleLock) return (_captureTask, _throttleReleaseTask); }
    }


    private uint _targetProcessId;
    private Process? _targetProcess;
    private ProcessNetworkScanner.ProcessEndpoint[] _scopedConnections = [];
    internal Func<bool>? TargetAliveOverride { get; set; }
    internal Func<uint, List<ProcessNetworkScanner.ProcessEndpoint>>? EndpointsOverride { get; set; }
    internal Func<ProcessNetworkScanner.ProcessEndpoint[]>? UdpFlowsOverride { get; set; }
    internal Func<ProcessNetworkScanner.ProcessEndpoint, bool>? UdpBindingOverride { get; set; }
    private ProcessNetworkScanner.ProcessEndpoint[] ReadUdpFlows() => UdpFlowsOverride?.Invoke() ?? _flowTracker.GetUdpFlows();
    private bool HasUdpBinding(ProcessNetworkScanner.ProcessEndpoint flow) =>
        UdpBindingOverride?.Invoke(flow) ?? _scanner.IsExclusiveUdpBinding(_targetProcessId, flow);
    internal int ObservedUdpFlowCount => ReadUdpFlows().Length;
    internal bool IsObservingUdp => _flowTracker.IsHealthy;
    private List<ProcessNetworkScanner.ProcessEndpoint> ReadEndpoints() =>
        EndpointsOverride?.Invoke(_targetProcessId) ?? _scanner.GetEndpointsForProcess(_targetProcessId);
    private FilterMode _filterMode = FilterMode.Outbound;
    private string _customFilter = "outbound";
    private int _dropPercentage = 100;
    private volatile bool _isRunning;
    private string _currentFilter = "false";

    // Throttle parameters
    private volatile bool _throttleEnabled = false;
    private int _throttleTimeframeMs = 1000; // Clumsy default
    private int _throttleJitterMs;
    private int _throttleChance = 100;       // Clumsy default
    private int _throttleReleaseDelayMs = 0; // 0 = Instant burst (Clumsy), >0 = Spaced release (speed control)

    // Diagnostic mode parameters
    private volatile DiagnosticMode _activeDiagnosticMode = DiagnosticMode.None;
    public DiagnosticMode ActiveDiagnosticMode
    {
        get => _activeDiagnosticMode;
        set
        {
            lock (_packetLock)
            {
                // Pending packets belong to the previous effect, never to its successor.
                if (_activeDiagnosticMode != value) FlushThrottleQueueLocked();
                _activeDiagnosticMode = value;
            }
        }
    }

    private int _globalDelayMs = 150;
    public int GlobalDelayMs
    {
        get => Volatile.Read(ref _globalDelayMs);
        set
        {
            lock (_packetLock)
            {
                Volatile.Write(ref _globalDelayMs, Math.Clamp(value, 0, 3000));
                // Rebase pending Lag packets on arrival, not slider-change time.
                // Keeps FIFO without the old 3000ms head blocking a new 50ms setting.
                if (_activeDiagnosticMode != DiagnosticMode.LatencyDelay) return;
                foreach (var packet in _throttleQueue)
                    packet.ReleaseAtTicks = packet.TimestampTicks + (long)_globalDelayMs * Stopwatch.Frequency / 1000;
                _nextReleaseTicks = 0;
                while (TryReleaseNext(_timestamp())) { }
            }
        }
    }

    private volatile int _burstLossPercentage = 50;
    public int BurstLossPercentage
    {
        get => _burstLossPercentage;
        set => _burstLossPercentage = Math.Clamp(value, 0, 100);
    }

    private volatile int _asymmetricInboundLossPercentage = 25;
    public int AsymmetricInboundLossPercentage
    {
        get => _asymmetricInboundLossPercentage;
        set => _asymmetricInboundLossPercentage = Math.Clamp(value, 0, 100);
    }

    private volatile int _asymmetricOutboundLossPercentage = 75;
    public int AsymmetricOutboundLossPercentage
    {
        get => _asymmetricOutboundLossPercentage;
        set => _asymmetricOutboundLossPercentage = Math.Clamp(value, 0, 100);
    }


    private readonly ConcurrentQueue<ThrottledPacket> _throttleQueue = new();
    private readonly object _handleLock = new();
    private readonly object _lifecycleLock = new();
    private int _safetyStopRequested;
    // Always acquire packet lock before handle lock: enqueue, release and flush are atomic.
    private readonly object _packetLock = new();
    private long _nextReleaseTicks;
    private volatile bool _stopping;
    // Narrow driver-free seams; production uses the native sender and monotonic clock.
    private Func<long> _timestamp = Stopwatch.GetTimestamp;
    private Func<IntPtr, uint, WinDivertNative.Address, bool>? _sendOverride = null;
    internal Func<IntPtr, uint, WinDivertNative.Address, bool>? SendOverride
    {
        get => _sendOverride;
        set => _sendOverride = value;
    }

    // Statistics
    private long _packetsIntercepted;
    private long _packetsDropped;
    private long _packetsPassedThrough;
    private long _packetsThrottled;
    private long _lastStatsNotificationTicks;
    private static readonly long StatsIntervalTicks = Math.Max(1, Stopwatch.Frequency / 4);

    public event Action<long, long, long, long>? StatsUpdated;
    public event Action<string>? ErrorOccurred;

    public long PacketsIntercepted => Volatile.Read(ref _packetsIntercepted);
    public long PacketsDropped => Volatile.Read(ref _packetsDropped);
    public long PacketsPassedThrough => Volatile.Read(ref _packetsPassedThrough);
    public long PacketsThrottled => Volatile.Read(ref _packetsThrottled);
    private long _tcpIntercepted, _udpIntercepted, _tcpDropped, _udpDropped, _tcpHeld, _udpHeld;
    public string ProtocolCounters => $"TCP: capturados {Volatile.Read(ref _tcpIntercepted)}, descartados {Volatile.Read(ref _tcpDropped)}, retidos {Volatile.Read(ref _tcpHeld)} | UDP: capturados {Volatile.Read(ref _udpIntercepted)}, descartados {Volatile.Read(ref _udpDropped)}, retidos {Volatile.Read(ref _udpHeld)} | pares UDP intencionalmente reordenados (tentativas): {IntentionallyReorderedPairs} | falhas de reinjeção: {PacketsFailedToSend}";
    public long UdpPacketsDropped => Volatile.Read(ref _udpDropped);
    public long UdpPacketsHeld => Volatile.Read(ref _udpHeld);
    public bool IsRunning => _isRunning;
    public string CurrentFilter => ScopeStatus.Filter;
    public DesyncStateMachine StateMachine => _stateMachine;

    private volatile bool _cycleDropEnabled = true;
    public bool CycleDropEnabled { get => _cycleDropEnabled; set => _cycleDropEnabled = value; }
    private volatile bool _throttleContinuous;
    public bool ThrottleContinuous
    {
        get => _throttleContinuous;
        set
        {
            lock (_packetLock)
            {
                _throttleContinuous = value;
                if (!IsThrottleEffective) FlushThrottleQueueLocked();
            }
        }
    }

    public bool ThrottleEnabled
    {
        get => _throttleEnabled;
        set
        {
            lock (_packetLock)
            {
                _throttleEnabled = value;
                if (!IsThrottleEffective) FlushThrottleQueueLocked();
            }
            if (!value) NotifyStats();
        }
    }

    public int ThrottleTimeframeMs
    {
        get => _throttleTimeframeMs;
        set => _throttleTimeframeMs = Math.Max(10, value);
    }

    /// <summary>Uniform per-packet offset in milliseconds; clamped to 0..2000.</summary>
    public int ThrottleJitterMs
    {
        get => Volatile.Read(ref _throttleJitterMs);
        set => Volatile.Write(ref _throttleJitterMs, Math.Clamp(value, 0, 2000));
    }

    public int ThrottleChance
    {
        get => _throttleChance;
        set => _throttleChance = Math.Clamp(value, 0, 100);
    }

    public int ThrottleReleaseDelayMs
    {
        get => _throttleReleaseDelayMs;
        set => _throttleReleaseDelayMs = Math.Clamp(value, 0, 200);
    }

    private volatile bool _isDropActive = false;
    private volatile bool _isThrottleActive = false;

    public bool IsDropActive
    {
        get => _isDropActive;
        set => _isDropActive = value;
    }

    public bool IsThrottleActive
    {
        get => _isThrottleActive;
        set
        {
            lock (_packetLock)
            {
                _isThrottleActive = value;
                if (!IsThrottleEffective) FlushThrottleQueueLocked();
            }
            if (!value) NotifyStats();
        }
    }

    public PacketEngine(
        ILogger<PacketEngine> logger,
        ProcessNetworkScanner scanner,
        FilterBuilder filterBuilder,
        FlowTracker flowTracker,
        DesyncStateMachine stateMachine)
    {
        _logger = logger;
        _scanner = scanner;
        _filterBuilder = filterBuilder;
        _flowTracker = flowTracker;
        _stateMachine = stateMachine;
        _flowTracker.Changed += SignalScopeChanged;
        _flowTracker.Failed += _ => SignalScopeChanged();

        // Auto flush throttle queue when leaving Active state
        _stateMachine.StateChanged += (oldState, newState) =>
        {
            if (oldState == DesyncStateMachine.State.Active && !IsThrottleEffective)
            {
                FlushThrottleQueue();
            }
        };
    }

    /// <summary>
    /// Starts packet interception using the specified filter mode and settings.
    /// </summary>
    public bool Start(
        FilterMode mode = FilterMode.Outbound,
        uint processId = 0,
        int dropPercentage = 100,
        string? customFilter = null)
    {
        lock (_lifecycleLock) return StartCore(mode, processId, dropPercentage, customFilter);
    }

    private bool StartCore(FilterMode mode, uint processId, int dropPercentage, string? customFilter)
    {
        if (mode == FilterMode.LegacyGlobalOutbound || _filterMode == FilterMode.LegacyGlobalOutbound)
        {
            ErrorOccurred?.Invoke("Captura GLOBAL bloqueada por esta API; use START/STOP no modo Outbound legado. A seleção isolada não inicia captura.");
            return false;
        }
        if (_disposed || _isRunning) return false;
        ObserveTargetCore(processId);
        if (_targetProcessId == 0)
        {
            ErrorOccurred?.Invoke("Captura bloqueada: " + ScopeStatus.Reason);
            return false;
        }
        Interlocked.Exchange(ref _safetyStopRequested, 0);
        _filterMode = mode;
        _dropPercentage = Math.Clamp(dropPercentage, 0, 100);
        _customFilter = customFilter ?? "outbound";
        _lastCaptureError = null;
        if (!_captureRequested)
        {
            Interlocked.Exchange(ref _packetsIntercepted, 0); Interlocked.Exchange(ref _packetsDropped, 0);
            Interlocked.Exchange(ref _packetsPassedThrough, 0); Interlocked.Exchange(ref _packetsThrottled, 0);
            Interlocked.Exchange(ref _lastStatsNotificationTicks, 0);
            Interlocked.Exchange(ref _tcpIntercepted, 0); Interlocked.Exchange(ref _udpIntercepted, 0);
            Interlocked.Exchange(ref _tcpDropped, 0); Interlocked.Exchange(ref _udpDropped, 0);
            Interlocked.Exchange(ref _tcpHeld, 0); Interlocked.Exchange(ref _udpHeld, 0);
        }
        _captureRequested = true;
        ReconcileScope();
        if (_captureRequested && !_isRunning && UdpFlowsOverride == null && !_flowTracker.IsHealthy)
        {
            _lastCaptureError = _flowTracker.LastError ?? "Observação FLOW indisponível; verifique driver/permissões.";
            StopCore();
            Volatile.Write(ref _scopeStatus, ScopeStatus with { Reason = _lastCaptureError });
            ErrorOccurred?.Invoke(_lastCaptureError);
        }
        return _isRunning;
    }

    /// <summary>Arm PID-only metadata before entering a service/experience; no payload capture.</summary>
    public void ObserveTarget(uint processId)
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            ObserveTargetCore(processId);
            ReconcileScope();
        }
    }

    private void ObserveTargetCore(uint pid)
    {
        bool same = pid != 0 && pid == _targetProcessId && TargetAlive();
        if (!same)
        {
            _lastCaptureError = null;
            StopCore();
            _flowTracker.Stop();
            _targetProcess?.Dispose(); _targetProcess = null;
            _targetProcessId = 0;
            if (pid == 0) { PublishEmpty(); return; }
            try
            {
                _targetProcess = Process.GetProcessById((int)pid);
                _ = _targetProcess.Handle;
                if (_targetProcess.HasExited) throw new InvalidOperationException("Processo encerrado");
                _targetProcessId = pid;
            }
            catch (Exception ex)
            {
                _targetProcess?.Dispose(); _targetProcess = null;
                _lastCaptureError = "Alvo indisponível: " + ex.Message;
                PublishEmpty(); return;
            }
        }
        if (UdpFlowsOverride == null) _flowTracker.Start(pid); // Same healthy PID preserves ledger/handle.
        _discoveryTask ??= Task.Run(DiscoveryLoop);
        SignalScopeChanged();
    }

    private bool TargetAlive()
    {
        try { return TargetAliveOverride?.Invoke() ?? (_targetProcess is { HasExited: false }); }
        catch { return false; }
    }

    private void SignalScopeChanged()
    {
        if (_disposed) return;
        try { _scopeSignal.Release(); }
        catch (SemaphoreFullException) { } // Coalesce metadata bursts.
    }

    private async Task DiscoveryLoop()
    {
        try
        {
            while (!_discoveryCts.IsCancellationRequested)
            {
                await _scopeSignal.WaitAsync(TimeSpan.FromMilliseconds(250), _discoveryCts.Token).ConfigureAwait(false);
                lock (_lifecycleLock)
                {
                    if (_disposed) return;
                    ReconcileScope();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { RequestSafetyStop("Falha na reconciliação do escopo: " + ex.Message); }
    }

    public bool Start(uint processId, int dropPercentage = 100)
    {
        return Start(FilterMode.Outbound, processId, dropPercentage);
    }

    public void UpdateFilter(FilterMode mode, uint processId = 0, string? customFilter = null)
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            if (_legacyPulseActive && mode != FilterMode.LegacyGlobalOutbound) StopCore();
            ObserveTargetCore(processId);
            _filterMode = mode;
            _customFilter = customFilter ?? "outbound";
            ReconcileScope();
        }
    }

    private void PublishEmpty() => Volatile.Write(ref _scopeStatus,
        CaptureScopeStatus.Empty with { Reason = _lastCaptureError ?? CaptureScopeStatus.Empty.Reason });

    // Single serialized control plane: callbacks never acquire this lock or join workers.
    private void ReconcileScope()
    {
        if (_filterMode == FilterMode.LegacyGlobalOutbound)
        {
            if (!_legacyPulseActive && (_isRunning || _captureRequested)) StopCore();
            PublishGlobalStatus();
            return; // Never derive global authorization from PID/port discovery.
        }
        if (_targetProcessId == 0) { PublishEmpty(); return; }
        if (!TargetAlive())
        {
            StopCore();
            _flowTracker.Stop();
            _targetProcess?.Dispose(); _targetProcess = null; _targetProcessId = 0;
            _lastCaptureError = "Processo-alvo encerrado: escopo e observação revogados.";
            PublishEmpty(); return;
        }
        try
        {
            var endpoints = ReadEndpoints();
            var tcp = endpoints.Where(e => e.Protocol == 6 && e.LocalPort != 0 && e.RemotePort != 0 && e.LocalAddr != 0 && e.RemoteAddr != 0).Distinct().ToArray();
            var udp = ReadUdpFlows();
            var eligibleUdp = UdpBindingOverride != null ? udp.Where(HasUdpBinding).ToArray() : _scanner.GetExclusiveUdpFlows(_targetProcessId, udp);
            var eligible = tcp.Concat(eligibleUdp).Distinct().OrderBy(e => e.Protocol).ThenBy(e => e.LocalAddr)
                .ThenBy(e => e.LocalPort).ThenBy(e => e.RemoteAddr).ThenBy(e => e.RemotePort).ToArray();
            string restriction = _filterMode == FilterMode.ProcessPorts ? "true" :
                _filterBuilder.GetFilter(_filterMode, null, _customFilter);
            if (IsScopedProfile(_armedScopeProfile))
            {
                // Intersect explicit user direction; never silently widen Outbound.
                string direction = _armedScopeProfile is DiagnosticMode.InboundBandwidthShaping or DiagnosticMode.CorrelatedInboundJitter
                    ? "(inbound or loopback)" : _armedScopeProfile == DiagnosticMode.AsymmetricDuplexShaping ? "true" : "outbound";
                string protocol = _armedScopeProfile is DiagnosticMode.PacketSizeAwareShaping or DiagnosticMode.ControlledPacketReordering ? "udp" : "(tcp or udp)";
                restriction = $"({restriction}) and not impostor and {direction} and {protocol}" +
                    (_armedScopeProfile == DiagnosticMode.ControlledPacketReordering ? " and loopback" : "");
            }
            string filter = !_captureRequested || restriction == "false" ? "false" :
                FilterBuilder.BuildScopedFilter(eligible, restriction, _armedScopeProfile is DiagnosticMode.InboundBandwidthShaping or DiagnosticMode.CorrelatedInboundJitter or DiagnosticMode.AsymmetricDuplexShaping);
            if (filter != _currentFilter || (_captureRequested && filter != "false" && !_isRunning))
                ReplaceNetworkScope(filter, eligible);
            if (!_captureRequested && _armedCapture) StopCore(); // Failed NETWORK open must revoke arming too.
            bool observing = UdpFlowsOverride != null || _flowTracker.IsHealthy;
            string reason = !observing ? (_flowTracker.LastError ?? "Observação FLOW indisponível; UDP não autorizado. Verifique driver/permissões.") :
                udp.Length == 0 ? "Nenhum fluxo UDP observado. Ative observação antes de entrar na experiência; saia e entre/reconecte. UDP antigo não é reconstruído." :
                eligibleUdp.Length == 0 ? "UDP observado, mas socket ausente, compartilhado ou propriedade ambígua: UDP recusado." :
                "Tuplas UDP verificadas; descobertas e revogações atualizam NETWORK automaticamente.";
            if (tcp.Length > 0 && _scopedConnections.All(e => e.Protocol != 17)) reason = "Somente TCP no escopo (HTTPS/controle não comprova canal da sessão). " + reason;
            if (_scopedConnections.All(e => e.Protocol != 17)) reason = "NENHUM UDP INCLUÍDO. " + reason;
            if (eligible.Length > 64) reason = "Limite de 64 tuplas excedido: captura bloqueada, sem truncar/ampliar.";
            if (_filterMode == FilterMode.Custom) reason += " Restrição personalizada adicional pode excluir protocolos/tuplas; confira contadores.";
            if (_lastCaptureError != null) reason = _lastCaptureError + " " + reason;
            Volatile.Write(ref _scopeStatus, new(_targetProcessId, observing, _captureRequested, _isRunning,
                tcp.Length, endpoints.Count(e => e.Protocol == 17), udp.Length, eligibleUdp.Length,
                _scopedConnections.Count(e => e.Protocol == 6), _scopedConnections.Count(e => e.Protocol == 17),
                reason, _currentFilter, "Elegíveis (até 64 no diagnóstico):\n" + CaptureScopeStatus.Describe(eligible.Take(64)) +
                "\nEscopadas em NETWORK:\n" + CaptureScopeStatus.Describe(_scopedConnections)));
        }
        catch (Exception ex)
        {
            _lastCaptureError = "Identidade/escopo indisponível: " + ex.Message;
            StopCore();
            Volatile.Write(ref _scopeStatus, ScopeStatus with { Running = false, TcpScoped = 0, UdpScoped = 0,
                Reason = _lastCaptureError, Filter = "false", Tuples = "" });
            ErrorOccurred?.Invoke(_lastCaptureError);
        }
    }

    /// <summary>
    /// Arms global outbound capture until STOP. The selection never applies an effect at START.
    /// </summary>
    public bool BeginLegacyGlobalContinuous(DiagnosticMode mode)
    {
        lock (_lifecycleLock)
        {
            string? refusal = _disposed ? "Motor encerrado." :
                !Enum.IsDefined(mode) ? "Modo inválido." :
                _filterMode != FilterMode.LegacyGlobalOutbound ? "Filtro GLOBAL não aplicado ao motor." :
                ProfileCompatibilityError(_armedScopeProfile, _filterMode) ??
                (_armedScopeProfile == DiagnosticMode.PacketSizeAwareShaping && !GlobalPacketSizeConsent
                    ? "Consentimento GLOBAL UDP obrigatório." :
                _isRunning || _legacyPulseActive || _captureRequested ? "Captura já solicitada; use STOP." :
                _stateMachine.Current != DesyncStateMachine.State.Idle ? "Motor não está Idle; use STOP antes de START." : null);
            if (refusal != null)
            {
                _lastCaptureError = "START GLOBAL recusado: " + refusal;
                PublishGlobalStatus();
                ErrorOccurred?.Invoke(_lastCaptureError);
                return false;
            }

            _legacyPulseActive = true;
            _captureRequested = true;
            PrepareArmedCapture();
            _lastCaptureError = null;
            Interlocked.Exchange(ref _safetyStopRequested, 0);
            // A permanent user-controlled lease. Only STOP, close, mode change, or failure ends it.
            Interlocked.Exchange(ref _legacyDeadlineTicks, long.MaxValue);
            Interlocked.Exchange(ref _packetsIntercepted, 0);
            Interlocked.Exchange(ref _packetsDropped, 0);
            Interlocked.Exchange(ref _packetsPassedThrough, 0);
            Interlocked.Exchange(ref _packetsThrottled, 0);
            Interlocked.Exchange(ref _tcpIntercepted, 0);
            Interlocked.Exchange(ref _udpIntercepted, 0);
            Interlocked.Exchange(ref _tcpDropped, 0);
            Interlocked.Exchange(ref _udpDropped, 0);
            Interlocked.Exchange(ref _tcpHeld, 0);
            Interlocked.Exchange(ref _udpHeld, 0);
            try
            {
                ReplaceNetworkScope("outbound", []);
                if (!_isRunning)
                {
                    StopCore();
                    PublishGlobalStatus();
                    return false;
                }
                PublishGlobalStatus();
                return true;
            }
            catch (Exception ex)
            {
                _lastCaptureError = "GLOBAL: falha ao iniciar: " + ex.Message;
                StopCore();
                PublishGlobalStatus();
                ErrorOccurred?.Invoke(_lastCaptureError);
                return false;
            }
        }
    }

    public bool BeginLegacyGlobalPulse(int durationMs, bool consent)
    {
        lock (_lifecycleLock)
        {
            int duration = Math.Clamp(durationMs, 0, 800);
            if (_disposed || !consent || duration == 0 || _legacyPulseActive ||
                _filterMode != FilterMode.LegacyGlobalOutbound || _stateMachine.Current != DesyncStateMachine.State.Active)
                return false;
            int generation = ++_legacyGeneration;
            Interlocked.Exchange(ref _legacyDeadlineTicks, long.MinValue); // Pass-through while the driver opens.
            _legacyPulseActive = true;
            _captureRequested = true;
            try
            {
                ReplaceNetworkScope("outbound", []); // EXACT old filter, only this explicit consented path.
                if (!_isRunning) { StopCore(); PublishGlobalStatus(); return false; }
                _legacyPulseTimer = new Timer(_ =>
                {
                    lock (_lifecycleLock)
                    {
                        if (_disposed || !_legacyPulseActive || generation != _legacyGeneration) return;
                        StopCore(); PublishGlobalStatus();
                    }
                }, null, Timeout.Infinite, Timeout.Infinite);
                Interlocked.Exchange(ref _legacyDeadlineTicks, Stopwatch.GetTimestamp() + duration * Stopwatch.Frequency / 1000);
                _legacyPulseTimer.Change(duration, Timeout.Infinite);
                PublishGlobalStatus();
                return true;
            }
            catch
            {
                StopCore(); PublishGlobalStatus();
                throw;
            }
        }
    }


    // Callbacks take only _packetLock, never _lifecycleLock (STOP joins workers).
    // Generations prevent a queued old timer restoring a new START or pulse.
    private bool _armedCapture;
    private long _armedGeneration;
    public long ArmedGeneration => Interlocked.Read(ref _armedGeneration);
    private long _effectGeneration;
    private long _effectDeadline;
    private long _effectStartedTicks;
    private long _shapingFinishTicks;
    private PulseShaper? _pulseShaper;
    private ScopedPulseScheduler? _scopedScheduler;
    private DiagnosticMode _armedScopeProfile;
    internal DiagnosticMode ArmedScopeProfile { get { lock (_lifecycleLock) return _armedScopeProfile; } }
    internal FilterMode ConfiguredFilterMode { get { lock (_lifecycleLock) return _filterMode; } }
    private long _reorderedPairs;
    public long IntentionallyReorderedPairs => Interlocked.Read(ref _reorderedPairs);
    private long _reinjectionFailures;
    public long PacketsFailedToSend => Interlocked.Read(ref _reinjectionFailures);
    // Explicit opt-in is session-only, never loaded from a settings file.
    private bool _experimentalReorderingConsent;
    public bool ExperimentalReorderingConsent
    {
        get { lock (_packetLock) return _experimentalReorderingConsent; }
        set
        {
            lock (_packetLock)
            {
                _experimentalReorderingConsent = value;
                if (!value && _activeDiagnosticMode == DiagnosticMode.ControlledPacketReordering) CancelArmedEffectLocked();
            }
        }
    }
    private long _scopedInboundHeld, _scopedOutboundHeld;
    public long ScopedInboundPacketsHeld => Interlocked.Read(ref _scopedInboundHeld);
    public long ScopedOutboundPacketsHeld => Interlocked.Read(ref _scopedOutboundHeld);
    public static bool IsScopedProfile(DiagnosticMode mode) => ScopedPulseScheduler.Handles(mode);
    public static string? ProfileCompatibilityError(DiagnosticMode mode, FilterMode filter)
    {
        if (filter == FilterMode.LegacyGlobalOutbound && IsScopedProfile(mode) && mode != DiagnosticMode.PacketSizeAwareShaping)
            return mode == DiagnosticMode.ControlledPacketReordering
                ? "INCOMPATÍVEL: reordering exige PID estrito, UDP loopback próprio e Opt-in EXPERIMENTAL; GLOBAL não é permitido. Selecione Saída do alvo e a aplicação."
                : "INCOMPATÍVEL: este efeito exige entrada ou duas direções e PID estrito. Selecione Entrada + saída do alvo ou Conexões verificadas da aplicação. Outbound não captura entrada.";
        if (filter == FilterMode.Outbound && mode is DiagnosticMode.InboundBandwidthShaping or DiagnosticMode.CorrelatedInboundJitter or DiagnosticMode.AsymmetricDuplexShaping)
            return "INCOMPATÍVEL: Saída do alvo captura somente saída. Este efeito exige entrada ou duas direções. Selecione Entrada + saída do alvo ou Conexões verificadas da aplicação; o filtro não será trocado automaticamente.";
        return null;
    }
    private bool _globalPacketSizeConsent;
    public bool GlobalPacketSizeConsent
    {
        get { lock (_packetLock) return _globalPacketSizeConsent; }
        set
        {
            lock (_packetLock)
            {
                _globalPacketSizeConsent = value;
                if (!value && _filterMode == FilterMode.LegacyGlobalOutbound && _activeDiagnosticMode == DiagnosticMode.PacketSizeAwareShaping)
                    CancelArmedEffectLocked();
            }
        }
    }
    public bool ConfigureScopedProfile(DiagnosticMode mode)
    {
        lock (_lifecycleLock)
        {
            if (_captureRequested || _isRunning) return false;
            _armedScopeProfile = IsScopedProfile(mode) ? mode : DiagnosticMode.None;
            return true;
        }
    }
    internal string? LastShapingFallback { get; private set; }
    private Timer? _effectTimer;
    public bool IsEffectActive
    {
        get
        {
            lock (_packetLock)
            {
                // New profiles publish completion only after timer/capture/release
                // has drained under this lock. Clock expiry alone is NOT a flush.
                // Capture/release still enforce the deadline before applying effects.
                bool active = _armedCapture && _stateMachine.Current == DesyncStateMachine.State.Active;
                return active && (IsScopedProfile(_armedScopeProfile) || _timestamp() < _effectDeadline);
            }
        }
    }

    private void PrepareArmedCapture()
    {
        lock (_packetLock)
        {
            CancelArmedEffectLocked();
            Interlocked.Increment(ref _armedGeneration);
            _armedCapture = true;
            _stateMachine.SetArmed();
        }
    }

    public bool StartArmed(FilterMode mode, uint processId, int dropPercentage, string? customFilter = null)
    {
        lock (_lifecycleLock)
        {
            if (_disposed || _captureRequested || mode == FilterMode.LegacyGlobalOutbound) return false;
            if (ProfileCompatibilityError(_armedScopeProfile, mode) is { } incompatibility)
            {
                _lastCaptureError = incompatibility;
                Volatile.Write(ref _scopeStatus, ScopeStatus with { Reason = incompatibility });
                ErrorOccurred?.Invoke(incompatibility);
                return false;
            }
            // Resolve identity first: target changes may stop the preceding epoch.
            ObserveTargetCore(processId);
            PrepareArmedCapture();
            bool started = StartCore(mode, processId, dropPercentage, customFilter);
            if (!_captureRequested) { lock (_packetLock) { _armedCapture = false; _stateMachine.ForceIdle(); } }
            return started;
        }
    }

    public bool TriggerArmedEffect(DiagnosticMode mode, int durationMs)
    {
        lock (_packetLock)
        {
            if (_disposed || !_armedCapture || !_captureRequested || !_isRunning || _stopping ||
                !Enum.IsDefined(mode) || durationMs <= 0) return false;
            ExpireArmedEffectLocked();
            if (_stateMachine.Current != DesyncStateMachine.State.Armed) return false;
            if (_filterMode == FilterMode.LegacyGlobalOutbound && mode == DiagnosticMode.None) return false;
            if (ProfileCompatibilityError(mode, _filterMode) != null) return false;
            if (_filterMode == FilterMode.LegacyGlobalOutbound && mode == DiagnosticMode.PacketSizeAwareShaping && !GlobalPacketSizeConsent) return false;
            if (IsScopedProfile(mode) && (_armedScopeProfile != mode ||
                (mode == DiagnosticMode.ControlledPacketReordering && !ExperimentalReorderingConsent))) return false;
            if (IsScopedProfile(_armedScopeProfile) && mode != _armedScopeProfile) return false;
            long generation = ++_effectGeneration;
            int duration = Math.Clamp(durationMs, 0, mode == DiagnosticMode.None && _filterMode != FilterMode.LegacyGlobalOutbound ? 10000 : 800);
            _effectStartedTicks = _timestamp();
            _shapingFinishTicks = _effectStartedTicks;
            _effectDeadline = _effectStartedTicks + (long)duration * Stopwatch.Frequency / 1000;
            _pulseShaper = PulseShaper.Handles(mode) ? new PulseShaper(mode, _effectStartedTicks, _effectDeadline) : null;
            _scopedScheduler = IsScopedProfile(mode) ? new ScopedPulseScheduler(mode, _effectStartedTicks) : null;
            LastShapingFallback = null;
            _activeDiagnosticMode = mode;
            _stateMachine.TryActivateArmed();
            _effectTimer?.Dispose();
            _effectTimer = new Timer(_ =>
            {
                lock (_packetLock)
                {
                    if (!_armedCapture || generation != _effectGeneration) return;
                    if (_timestamp() < _effectDeadline)
                    {
                        int remaining = (int)Math.Max(1, (_effectDeadline - _timestamp()) * 1000 / Stopwatch.Frequency);
                        _effectTimer?.Change(remaining, Timeout.Infinite);
                        return;
                    }
                    CancelArmedEffectLocked();
                }
            }, null, duration, Timeout.Infinite);
            return true;
        }
    }

    public void CancelArmedEffect()
    {
        lock (_packetLock) { if (_armedCapture) CancelArmedEffectLocked(); }
    }

    private void ExpireArmedEffectLocked()
    {
        if (_armedCapture && _stateMachine.Current == DesyncStateMachine.State.Active && _timestamp() >= _effectDeadline)
            CancelArmedEffectLocked();
    }

    private void CancelArmedEffectLocked()
    {
        Interlocked.Increment(ref _armedGeneration); // Also revokes queued hotkeys on profile cancellation.
        ++_effectGeneration;
        _effectTimer?.Dispose(); _effectTimer = null;
        _effectDeadline = 0;
        _activeDiagnosticMode = DiagnosticMode.None;
        if (_armedCapture) _stateMachine.SetArmed();
        FlushThrottleQueueLocked();
    }

    public void EndLegacyGlobalPulse()
    {
        lock (_lifecycleLock)
        {
            if (_legacyPulseActive) StopCore();
            if (_filterMode == FilterMode.LegacyGlobalOutbound) PublishGlobalStatus();
        }
    }

    private void PublishGlobalStatus() => Volatile.Write(ref _scopeStatus,
        new CaptureScopeStatus(0, false, _legacyPulseActive, _isRunning, 0, 0, 0, 0, 0, 0,
            _lastCaptureError ?? "GLOBAL — todas as conexões de saída do Windows. START arma sem efeito; Espaço dispara um pulso. Ao terminar, captura permanece armada até STOP; não identifica Roblox.",
            _legacyPulseActive && _isRunning ? "outbound" : "false", "GLOBAL: filtro literal outbound, SEM restrição por PID. START/STOP contínuo."));

    // WinDivert has no in-place filter update. Break-before-make: no overlapping
    // handles/double effects. During the bounded turnover traffic passes normally.
    // Publish new scope only after draining and closing the old send-capable epoch.
    private void ReplaceNetworkScope(string filter, ProcessNetworkScanner.ProcessEndpoint[] eligible)
    {
        _reconfiguring = true;
        try
        {
            RetireCaptureEpoch(); // No packet/handle lock held while joining Recv.
            lock (_packetLock)
            {
                if (IsScopedProfile(_armedScopeProfile)) CancelArmedEffectLocked();
                else FlushThrottleQueueLocked();
                CloseNetworkHandle();
                _scopedConnections = [];
                _currentFilter = "false";
                _isRunning = false;
                if (filter != "false" && OpenNetworkHandle(filter))
                {
                    _cts ??= new CancellationTokenSource();
                    _stopping = false;
                    _scopedConnections = eligible;
                    _currentFilter = filter;
                    _isRunning = true;
                    var epoch = _epoch = new CaptureEpoch(_networkHandle);
                    var token = _cts.Token;
                    _captureTask = Task.Factory.StartNew(() => CaptureLoop(token, epoch), CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    _throttleReleaseTask ??= Task.Run(() => ThrottleReleaseLoop(token));
                }
                else if (filter != "false") _captureRequested = false;
            }
        }
        finally { _reconfiguring = false; }
    }

    private void RetireCaptureEpoch()
    {
        if (_epoch is { } epoch)
        {
            epoch.Retiring = true;
            WinDivertNative.WinDivertShutdown(epoch.Handle, WinDivertNative.Shutdown.Recv);
        }
        _captureTask?.GetAwaiter().GetResult();
        _captureTask = null; _epoch = null;
    }

    public void Stop()
    {
        lock (_lifecycleLock) { if (!_disposed) StopCore(); }
    }

    private void StopCore()
    {
        _legacyPulseActive = false;
        ++_legacyGeneration;
        _legacyPulseTimer?.Dispose(); _legacyPulseTimer = null;
        _captureRequested = false;
        lock (_packetLock)
        {
            CancelArmedEffectLocked();
            _armedCapture = false;
            Interlocked.Increment(ref _armedGeneration);
            _stopping = true;
            _isRunning = false;
            _isDropActive = false;
            _isThrottleActive = false;
        }
        _cts?.Cancel();
        RetireCaptureEpoch();
        _throttleReleaseTask?.GetAwaiter().GetResult();
        _throttleReleaseTask = null;
        lock (_packetLock)
        {
            FlushThrottleQueueLocked();
            CloseNetworkHandle();
            _scopedConnections = [];
            _currentFilter = "false";
        }
        NotifyStats();
        _cts?.Dispose();
        _cts = null;
        _stateMachine.ForceIdle();
        ActiveDiagnosticMode = DiagnosticMode.None;
        // Preserve the same PID's observer/ledger for restart. Only target change,
        // process exit, explicit ObserveTarget(0) or Dispose revokes observation.
        Volatile.Write(ref _scopeStatus, ScopeStatus with { Requested = false, Running = false,
            TcpScoped = 0, UdpScoped = 0, Filter = "false", Tuples = "" });
    }

    public void SetDropPercentage(int percentage)
    {
        _dropPercentage = Math.Clamp(percentage, 0, 100);
    }

    // Failure-only seam: never supplies a fake successful native handle.
    internal Func<string, int>? NetworkOpenFailureOverride { get; set; }
    private bool OpenNetworkHandle(string filter)
    {
        lock (_handleLock)
        {
            int injectedError = NetworkOpenFailureOverride?.Invoke(filter) ?? 0;
            _networkHandle = injectedError != 0 ? WinDivertNative.InvalidHandle : WinDivertNative.WinDivertOpen(
                filter,
                WinDivertNative.Layer.Network,
                (short)0,
                0);

            if (_networkHandle == WinDivertNative.InvalidHandle)
            {
                int err = injectedError != 0 ? injectedError : Marshal.GetLastWin32Error();
                _logger.LogError("WinDivertOpen failed for filter '{Filter}': Win32 error {Error}", filter, err);

                string errorMsg = err switch
                {
                    2 => "Arquivos do driver WinDivert não encontrados (WinDivert.dll / WinDivert64.sys).",
                    5 => "Acesso negado. O DarkDesync precisa ser executado como Administrador para carregar o driver de rede.",
                    87 => $"Filtro WinDivert inválido: '{filter}'",
                    577 => "O driver WinDivert não possui assinatura válida no sistema.",
                    _ => $"Erro WinDivert #{err}"
                };

                _lastCaptureError = $"{errorMsg} (Win32 {err})";
                ErrorOccurred?.Invoke(_lastCaptureError);
                return false;
            }

            WinDivertNative.WinDivertSetParam(_networkHandle, WinDivertNative.Param.QueueLength, 8192);
            WinDivertNative.WinDivertSetParam(_networkHandle, WinDivertNative.Param.QueueTime, 1000);
            WinDivertNative.WinDivertSetParam(_networkHandle, WinDivertNative.Param.QueueSize, 4194304);

            return true;
        }
    }

    private void CloseNetworkHandle()
    {
        lock (_handleLock)
        {
            if (_networkHandle != WinDivertNative.InvalidHandle)
            {
                WinDivertNative.WinDivertClose(_networkHandle);
                _networkHandle = WinDivertNative.InvalidHandle;
            }
        }
    }

    /// <summary>
    /// High-performance packet capture loop.
    /// Intercepts packets and evaluates Drop vs Throttle vs Pass-through.
    /// </summary>
    private void CaptureLoop(CancellationToken ct, CaptureEpoch epoch)
    {
        int bufferSize = WinDivertNative.MtuMax;
        IntPtr packetBuffer = Marshal.AllocHGlobal(bufferSize);
        var random = new Random();
        int statCounter = 0;

        try
        {
            // Shutdown(Recv) stops new interceptions. Drain pending packets while
            // the handle still supports sending, instead of dropping them on close.
            while (true)
            {
                var addr = new WinDivertNative.Address();
                bool success = WinDivertNative.WinDivertRecv(
                    epoch.Handle, packetBuffer, (uint)bufferSize, out uint recvLen, ref addr);

                if (!success)
                {
                    if (epoch.Retiring || ct.IsCancellationRequested || !_isRunning)
                        break;

                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "WinDivertRecv failed");
                }

                bool affected = ProcessCapturedPacket(packetBuffer, recvLen, addr, random);

                statCounter++;
                if (affected || statCounter >= 25)
                {
                    statCounter = 0;
                    NotifyStats();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Capture loop exception");
            RequestSafetyStop($"Captura interrompida e tráfego restaurado: {ex.Message}");
        }
        finally
        {
            Marshal.FreeHGlobal(packetBuffer);
        }
    }

    internal bool ProcessCapturedPacket(IntPtr packetBuffer, uint recvLen, WinDivertNative.Address addr, Random random)
    {
        lock (_packetLock)
        {
            byte protocol = recvLen >= 20 && Marshal.ReadByte(packetBuffer, 0) >> 4 == 4 ? Marshal.ReadByte(packetBuffer, 9) : (byte)0;
            long dropped = _packetsDropped, held = _packetsThrottled;
            bool affected = ProcessCapturedPacketCore(packetBuffer, recvLen, addr, random);
            if (protocol == 6)
            {
                Interlocked.Increment(ref _tcpIntercepted);
                Interlocked.Add(ref _tcpDropped, _packetsDropped - dropped);
                Interlocked.Add(ref _tcpHeld, _packetsThrottled - held);
            }
            else if (protocol == 17)
            {
                Interlocked.Increment(ref _udpIntercepted);
                Interlocked.Add(ref _udpDropped, _packetsDropped - dropped);
                Interlocked.Add(ref _udpHeld, _packetsThrottled - held);
            }
            return affected;
        }
    }

    private bool ProcessCapturedPacketCore(IntPtr packetBuffer, uint recvLen, WinDivertNative.Address addr, Random random)
    {
        lock (_packetLock)
        {
            Interlocked.Increment(ref _packetsIntercepted);
            // A Recv finishing during Stop must not overtake packets already held.
            if (_stopping || _reconfiguring)
            {
                FlushThrottleQueueLocked();
                RecordSend(SendPacket(packetBuffer, recvLen, addr));
                return false;
            }

            ExpireArmedEffectLocked();
            bool active = _stateMachine.Current == DesyncStateMachine.State.Active;
            if (_armedCapture && !active)
            {
                RecordSend(SendPacket(packetBuffer, recvLen, addr));
                return false; // Saved drop/throttle flags cannot apply effects at START.
            }
            bool legacy = _legacyPulseActive;
            if (legacy && !addr.Outbound)
            {
                RecordSend(SendPacket(packetBuffer, recvLen, addr));
                return false; // Unexpected inbound must not flush the outbound effect queue.
            }
            if (legacy && (!WithinLegacyDeadline || !active))
            {
                FlushThrottleQueueLocked();
                RecordSend(SendPacket(packetBuffer, recvLen, addr));
                return false; // Even a delayed watchdog/controller cannot affect packets outside the lease.
            }

            if (IsScopedProfile(_armedScopeProfile))
                return ProcessScopedPacket(packetBuffer, recvLen, addr, active);
            if (IsScopedProfile(_activeDiagnosticMode))
            { RecordSend(SendPacket(packetBuffer, recvLen, addr)); return false; }

            // All effects, including legacy drop/throttle, require live ownership.
            // Driver-free scheduling tests use SendOverride without starting capture.
            if (!legacy && (_isRunning || _activeDiagnosticMode != DiagnosticMode.None) &&
                !IsAuthorizedTraffic(packetBuffer, recvLen, addr))
            {
                RecordSend(SendPacket(packetBuffer, recvLen, addr));
                return false;
            }

            // Diagnostic Mode evaluation: restricted strictly to authorized test traffic
            if (!_stopping && active && _activeDiagnosticMode != DiagnosticMode.None)
            {
                switch (_activeDiagnosticMode)
                {
                    case DiagnosticMode.HoldAndDrainShaping:
                    case DiagnosticMode.RateStepShaping:
                    case DiagnosticMode.DualTokenBucket:
                    case DiagnosticMode.FlowAwareShaping:
                    {
                        if (!addr.Outbound || !_armedCapture)
                        {
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                        PulseShaper.Flow flow = default;
                        bool identifiable = _activeDiagnosticMode != DiagnosticMode.FlowAwareShaping ||
                            (recvLen >= 20 && Marshal.ReadByte(packetBuffer, 0) >> 4 == 4 &&
                             TryExtractIpAndPorts(packetBuffer, recvLen, out var source, out var destination,
                                 out var sourcePort, out var destinationPort) &&
                             SetFlow(out flow, Marshal.ReadByte(packetBuffer, 9), source!, sourcePort, destination!, destinationPort));
                        if (!identifiable || _pulseShaper == null || recvLen == 0 || recvLen > 1536 || _pulseShaper.Count >= 512)
                        {
                            LastShapingFallback = $"mode={_activeDiagnosticMode}; identifiable={identifiable}; IP bytes={recvLen}; queued={_pulseShaper?.Count}";
                            _logger.LogWarning("Shaping fail-open: {Reason}", LastShapingFallback);
                            CancelArmedEffectLocked();
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                        var data = new byte[recvLen];
                        Marshal.Copy(packetBuffer, data, 0, (int)recvLen);
                        var packet = new ThrottledPacket { Data = data, Length = recvLen, Address = addr, TimestampTicks = _timestamp() };
                        if (!_pulseShaper.Enqueue(packet, flow))
                        {
                            LastShapingFallback = $"mode={_activeDiagnosticMode}; flow/burst capacity; IP bytes={recvLen}; queued={_pulseShaper.Count}";
                            _logger.LogWarning("Shaping fail-open: {Reason}", LastShapingFallback);
                            CancelArmedEffectLocked();
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                        Interlocked.Increment(ref _packetsThrottled);
                        return true;
                    }
                    case DiagnosticMode.OutboundFreeze:
                    {
                        if (addr.Outbound)
                        {
                            {
                                // Bound retained memory even under a saturated sender.
                                if (_throttleQueue.Count >= 512 || recvLen > WinDivertNative.MtuMax)
                                {
                                    RequestSafetyStop("Freeze: limite de retenção atingido; STOP e liberação por segurança.");
                                    FlushThrottleQueueLocked();
                                    RecordSend(SendPacket(packetBuffer, recvLen, addr));
                                    return false;
                                }
                                var packetCopy = new byte[recvLen];
                                Marshal.Copy(packetBuffer, packetCopy, 0, (int)recvLen);
                                long now = _timestamp();
                                _throttleQueue.Enqueue(new ThrottledPacket
                                {
                                    Data = packetCopy,
                                    Length = recvLen,
                                    Address = addr,
                                    TimestampTicks = now,
                                    ReleaseAtTicks = long.MaxValue // Released when cycle ends and queue is flushed
                                });
                                Interlocked.Increment(ref _packetsThrottled);
                                return true;
                            }
                        }
                        else
                        {
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                    }

                    case DiagnosticMode.BurstBuffering:
                    case DiagnosticMode.SlottedTransmission:
                    case DiagnosticMode.SawtoothDelay:
                    case DiagnosticMode.BandwidthShaping:
                    case DiagnosticMode.AdaptiveFreeze:
                    {
                        // New variants require an explicit armed pulse, never continuous flags.
                        if (!addr.Outbound || !_armedCapture)
                        {
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                        if (_throttleQueue.Count >= 512 || recvLen > WinDivertNative.MtuMax)
                        {
                            // Fail open for the remainder of this pulse, with a complete FIFO flush.
                            CancelArmedEffectLocked();
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            _logger.LogWarning("Buffering saturation: pulse cancelled; FIFO flushed (fail-open).");
                            return false;
                        }
                        long now = _timestamp();
                        long elapsed = Math.Max(0, now - _effectStartedTicks);
                        long Ms(int ms) => Math.Max(1, (long)ms * Stopwatch.Frequency / 1000);
                        long releaseAt;
                        switch (_activeDiagnosticMode)
                        {
                            case DiagnosticMode.BurstBuffering:
                                // Accumulate complete 80ms batches, not a hold until pulse end.
                                releaseAt = _effectStartedTicks + (elapsed / Ms(80) + 1) * Ms(80);
                                break;
                            case DiagnosticMode.SlottedTransmission:
                                // 10ms open windows every 60ms; closed windows queue to next slot.
                                releaseAt = elapsed % Ms(60) < Ms(10) ? now :
                                    _effectStartedTicks + (elapsed / Ms(60) + 1) * Ms(60);
                                break;
                            case DiagnosticMode.SawtoothDelay:
                                // Delay ramps 0..120ms over each 160ms tooth; FIFO clips overtaking.
                                releaseAt = now + elapsed % Ms(160) * 3 / 4;
                                break;
                            case DiagnosticMode.BandwidthShaping:
                                // Virtual serialization at 16KiB/s, charged in IP bytes (no token burst).
                                _shapingFinishTicks = Math.Max(now, _shapingFinishTicks) +
                                    Math.Max(1, ((long)recvLen * Stopwatch.Frequency + 16383) / 16384);
                                releaseAt = _shapingFinishTicks;
                                break;
                            default: // AdaptiveFreeze: at most 90ms per group or 32 packets.
                                releaseAt = _throttleQueue.TryPeek(out var head) ? head.ReleaseAtTicks : now + Ms(90);
                                if (_throttleQueue.Count >= 31)
                                {
                                    FlushThrottleQueueLocked();
                                    releaseAt = now;
                                }
                                break;
                        }
                        var data = new byte[recvLen];
                        Marshal.Copy(packetBuffer, data, 0, (int)recvLen);
                        _throttleQueue.Enqueue(new ThrottledPacket
                        {
                            Data = data, Length = recvLen, Address = addr, TimestampTicks = now,
                            ReleaseAtTicks = Math.Min(releaseAt, _effectDeadline)
                        });
                        Interlocked.Increment(ref _packetsThrottled);
                        return true;
                    }

                    case DiagnosticMode.MicroFreeze:
                    case DiagnosticMode.SmoothDelay:
                    case DiagnosticMode.SoftJitter:
                    {
                        if (!addr.Outbound)
                        {
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                        // Saturation shortens the oldest hold, never discards or overtakes it.
                        if (_throttleQueue.Count >= 512 && _throttleQueue.TryDequeue(out var oldest))
                            SendThrottledPacket(oldest);
                        if (recvLen > WinDivertNative.MtuMax)
                        {
                            FlushThrottleQueueLocked();
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                        long now = _timestamp();
                        long releaseAt;
                        if (_activeDiagnosticMode == DiagnosticMode.MicroFreeze)
                        {
                            long interval = Math.Max(1, 20L * Stopwatch.Frequency / 1000);
                            releaseAt = _effectStartedTicks + ((now - _effectStartedTicks) / interval + 1) * interval;
                        }
                        else
                        {
                            int delayMs = _activeDiagnosticMode == DiagnosticMode.SmoothDelay ? 30 : random.Next(8, 25);
                            releaseAt = now + (long)delayMs * Stopwatch.Frequency / 1000;
                        }
                        var data = new byte[recvLen];
                        Marshal.Copy(packetBuffer, data, 0, (int)recvLen);
                        _throttleQueue.Enqueue(new ThrottledPacket
                        {
                            Data = data, Length = recvLen, Address = addr, TimestampTicks = now,
                            ReleaseAtTicks = _armedCapture ? Math.Min(releaseAt, _effectDeadline) : releaseAt
                        });
                        Interlocked.Increment(ref _packetsThrottled);
                        return true;
                    }

                    case DiagnosticMode.LatencyDelay:
                    {
                        if (!addr.Outbound || GlobalDelayMs == 0)
                        {
                            if (addr.Outbound) FlushThrottleQueueLocked();
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                        // Fail open in FIFO order: release oldest early, never let new traffic overtake it.
                        if (_throttleQueue.Count >= 512 && _throttleQueue.TryDequeue(out var oldest))
                            SendThrottledPacket(oldest);
                        if (recvLen > WinDivertNative.MtuMax)
                        {
                            FlushThrottleQueueLocked();
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                        var data = new byte[recvLen];
                        Marshal.Copy(packetBuffer, data, 0, (int)recvLen);
                        long now = _timestamp();
                        _throttleQueue.Enqueue(new ThrottledPacket
                        {
                            Data = data,
                            Length = recvLen,
                            Address = addr,
                            TimestampTicks = now,
                            ReleaseAtTicks = now + (long)GlobalDelayMs * Stopwatch.Frequency / 1000
                        });
                        Interlocked.Increment(ref _packetsThrottled);
                        return true;
                    }

                    case DiagnosticMode.PacketDrop:
                    case DiagnosticMode.BurstLoss:
                    {
                        int pct = _activeDiagnosticMode == DiagnosticMode.PacketDrop ? _dropPercentage : _burstLossPercentage;
                        bool drop = pct >= 100 || (pct > 0 && random.Next(100) < pct);
                        if (drop)
                        {
                            Interlocked.Increment(ref _packetsDropped);
                            return true;
                        }
                        else
                        {
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                    }

                    case DiagnosticMode.AsymmetricLoss:
                    {
                        int pct = addr.Outbound
                            ? Math.Clamp(_asymmetricOutboundLossPercentage, 0, 100)
                            : Math.Clamp(_asymmetricInboundLossPercentage, 0, 100);

                        bool drop = pct >= 100 || (pct > 0 && random.Next(100) < pct);
                        if (drop)
                        {
                            Interlocked.Increment(ref _packetsDropped);
                            return true;
                        }
                        else
                        {
                            RecordSend(SendPacket(packetBuffer, recvLen, addr));
                            return false;
                        }
                    }
                }
            }

            // Disabled GLOBAL is pass-through. Legacy profile flags cannot secretly turn it into loss.
            if (_filterMode == FilterMode.LegacyGlobalOutbound)
            {
                RecordSend(SendPacket(packetBuffer, recvLen, addr));
                return false;
            }

            bool shouldDrop = false;
            bool shouldThrottle = false;

            bool isThrottleEffective = !_stopping && (_isThrottleActive || (_throttleEnabled && (_throttleContinuous || active)));
            // Evaluate both modes against the same state snapshot. Explicit drop wins.
            bool isDropEffective = !_stopping && (_isDropActive || (CycleDropEnabled && active && !isThrottleEffective));
            if (isDropEffective && _dropPercentage > 0)
            {
                if (_dropPercentage >= 100 || random.Next(100) < _dropPercentage)
                {
                    shouldDrop = true;
                }
            }

            // 2. Throttle check (if packet is not dropped)
            if (!shouldDrop && isThrottleEffective)
            {
                if (_throttleChance >= 100 || (_throttleChance > 0 && random.Next(100) < _throttleChance))
                {
                    shouldThrottle = true;
                }
            }

            if (shouldDrop)
            {
                // DROP PACKET: Do not call WinDivertSend
                Interlocked.Increment(ref _packetsDropped);
            }
            else if (shouldThrottle)
            {
                if (_throttleQueue.Count >= 512 && _throttleQueue.TryDequeue(out var oldest))
                    SendThrottledPacket(oldest);
                if (recvLen > WinDivertNative.MtuMax)
                {
                    FlushThrottleQueueLocked();
                    RecordSend(SendPacket(packetBuffer, recvLen, addr));
                    return false;
                }
                // THROTTLE PACKET: Queue packet in memory
                var packetCopy = new byte[recvLen];
                Marshal.Copy(packetBuffer, packetCopy, 0, (int)recvLen);

                long now = _timestamp();
                int jitter = ThrottleJitterMs;
                long delayMs = Math.Max(0L, (long)ThrottleTimeframeMs + (jitter == 0 ? 0 : random.Next(-jitter, jitter + 1)));
                _throttleQueue.Enqueue(new ThrottledPacket
                {
                    Data = packetCopy,
                    Length = recvLen,
                    Address = addr,
                    TimestampTicks = now,
                    ReleaseAtTicks = now + delayMs * Stopwatch.Frequency / 1000
                });

                Interlocked.Increment(ref _packetsThrottled);
            }
            else
            {
                RecordSend(SendPacket(packetBuffer, recvLen, addr));
            }

            return shouldDrop || shouldThrottle;
        }
    }

    /// <summary>
    /// Requires a full owned TCP tuple, or a current UDP PID FLOW plus exclusive binding.
    /// </summary>
    public bool IsAuthorizedTraffic(IntPtr packetBuffer, uint recvLen, WinDivertNative.Address addr)
    {
        if (!TryExtractIpAndPorts(packetBuffer, recvLen, out var srcIp, out var dstIp, out var srcPort, out var dstPort))
        {
            // If headers cannot be parsed, pass through safely without drop
            return false;
        }

        byte protocol = Marshal.ReadByte(packetBuffer, 9);
        if (Marshal.ReadByte(packetBuffer, 0) >> 4 != 4 || (protocol != 6 && protocol != 17))
            return false;
        try
        {
            if (!(TargetAliveOverride?.Invoke() ?? (_targetProcess != null && !_targetProcess.HasExited)))
            {
                RequestSafetyStop("Captura parada: processo-alvo encerrado ou identidade indisponível.");
                return false;
            }
            var localIp = addr.Outbound ? srcIp : dstIp;
            var remoteIp = addr.Outbound ? dstIp : srcIp;
            ushort localPort = addr.Outbound ? srcPort : dstPort;
            ushort remotePort = addr.Outbound ? dstPort : srcPort;
            if (protocol == 17)
            {
                var flow = ReadUdpFlows().FirstOrDefault(e => e.Protocol == 17 && e.LocalPort == localPort &&
                    e.RemotePort == remotePort && new System.Net.IPAddress(e.LocalAddr).Equals(localIp) &&
                    new System.Net.IPAddress(e.RemoteAddr).Equals(remoteIp));
                if (flow != null && HasUdpBinding(flow)) return true;
                SignalScopeChanged(); // Pass through now; revoke only this tuple, not unrelated scopes/ledger.
                return false;
            }
            // Revalidate the full tuple, not either port or an arbitrary loopback packet.
            var owned = ReadEndpoints();
            if (!owned.Any(e => e.Protocol == 6 && e.RemotePort != 0) && ReadUdpFlows().Length == 0)
                SignalScopeChanged();
            return owned.Any(e => e.Protocol == 6 && e.LocalPort == localPort &&
                e.RemotePort == remotePort && new System.Net.IPAddress(e.LocalAddr).Equals(localIp) &&
                new System.Net.IPAddress(e.RemoteAddr).Equals(remoteIp));
        }
        catch
        {
            SignalScopeChanged();
            return false;
        }
    }

    private static bool SetFlow(out PulseShaper.Flow flow, byte protocol, System.Net.IPAddress source,
        ushort sourcePort, System.Net.IPAddress destination, ushort destinationPort)
    {
        flow = new(protocol, source.ToString(), sourcePort, destination.ToString(), destinationPort);
        return sourcePort != 0 && destinationPort != 0;
    }

    private bool ProcessScopedPacket(IntPtr buffer, uint length, WinDivertNative.Address native, bool active)
    {
        // Same-handle reinjection cannot be recaptured; refuse other injectors too.
        bool globalSize = _filterMode == FilterMode.LegacyGlobalOutbound &&
            _armedScopeProfile == DiagnosticMode.PacketSizeAwareShaping && GlobalPacketSizeConsent;
        // The literal GLOBAL outbound filter also captures unrelated TCP,
        // IPv6 and ICMP. These protocols are disjoint from IPv4 UDP shaping:
        // pass them through without cancelling an active UDP pulse. Malformed
        // IPv4 UDP and impostor packets retain conservative fail-open/cancel.
        if (globalSize && !native.Impostor && length >= 20 &&
            (Marshal.ReadByte(buffer, 0) >> 4 != 4 || Marshal.ReadByte(buffer, 9) != 17))
        { RecordSend(SendPacket(buffer, length, native)); return false; }
        bool outbound; PulseShaper.Flow flow;
        bool identified = globalSize
            ? TryGlobalSizeFlow(buffer, length, native, out outbound, out flow)
            : TryScopedDirection(buffer, length, native, out outbound, out flow);
        if (native.Impostor || (_filterMode == FilterMode.LegacyGlobalOutbound && !globalSize) || !identified)
        {
            // Loss of proof must not allow a newer packet to overtake a retained one.
            CancelArmedEffectLocked(); RecordSend(SendPacket(buffer, length, native)); return false;
        }
        if (!active || _activeDiagnosticMode != _armedScopeProfile || _scopedScheduler == null ||
            (_activeDiagnosticMode is DiagnosticMode.InboundBandwidthShaping or DiagnosticMode.CorrelatedInboundJitter && outbound) ||
            (_activeDiagnosticMode is DiagnosticMode.PacketSizeAwareShaping or DiagnosticMode.ControlledPacketReordering && (!outbound || flow.Protocol != 17)) ||
            (_activeDiagnosticMode == DiagnosticMode.ControlledPacketReordering && (!native.Loopback || !ExperimentalReorderingConsent)))
        { RecordSend(SendPacket(buffer, length, native)); return false; }
        if (length is 0 or > 1536)
        { CancelArmedEffectLocked(); RecordSend(SendPacket(buffer, length, native)); return false; }
        var data = new byte[length]; Marshal.Copy(buffer, data, 0, (int)length);
        var packet = new ThrottledPacket { Data = data, Length = length, Address = native, TimestampTicks = _timestamp() };
        if (!_scopedScheduler.Enqueue(packet, flow, packet.TimestampTicks))
        {
            LastShapingFallback = $"{_activeDiagnosticMode}: bounded queue/flow/byte capacity; fail-open";
            CancelArmedEffectLocked(); RecordSend(SendPacket(buffer, length, native)); return false;
        }
        Interlocked.Increment(ref _packetsThrottled);
        if (outbound) Interlocked.Increment(ref _scopedOutboundHeld); else Interlocked.Increment(ref _scopedInboundHeld);
        return true;
    }

    private bool TryScopedDirection(IntPtr buffer, uint length, WinDivertNative.Address native,
        out bool outbound, out PulseShaper.Flow flow)
    {
        outbound = native.Outbound; flow = default;
        if (!TryExtractIpAndPorts(buffer, length, out var source, out var destination, out var sp, out var dp) ||
            Marshal.ReadByte(buffer, 0) >> 4 != 4 || sp == 0 || dp == 0) return false;
        byte protocol = Marshal.ReadByte(buffer, 9);
        // Loopback is always native outbound. Prove full ownership in both directions;
        // both endpoints owned by this PID is ambiguous and MUST fail open.
        if (native.Loopback)
        {
            if (!System.Net.IPAddress.IsLoopback(source!) || !System.Net.IPAddress.IsLoopback(destination!)) return false;
            var forward = native; forward.Flags |= 1u << 17;
            var reverse = native; reverse.Flags &= ~(1u << 17);
            bool ownsForward = IsAuthorizedTraffic(buffer, length, forward);
            bool ownsReverse = IsAuthorizedTraffic(buffer, length, reverse);
            if (ownsForward == ownsReverse) return false;
            outbound = ownsForward;
        }
        else if (!IsAuthorizedTraffic(buffer, length, native)) return false;
        flow = new(protocol, (outbound ? "out:" : "in:") + source, sp, destination!.ToString(), dp);
        return true;
    }

    private static bool TryGlobalSizeFlow(IntPtr buffer, uint length, WinDivertNative.Address native,
        out bool outbound, out PulseShaper.Flow flow)
    {
        outbound = native.Outbound; flow = default;
        // Explicit GLOBAL consent replaces PID proof, not packet validation. TCP,
        // IPv6, fragments and other injectors fail open; no replay or intentional loss.
        if (!outbound || native.Impostor || !TryExtractIpAndPorts(buffer, length, out var source, out var destination, out var sp, out var dp) ||
            Marshal.ReadByte(buffer, 0) >> 4 != 4 || Marshal.ReadByte(buffer, 9) != 17 || sp == 0 || dp == 0) return false;
        flow = new(17, "out:" + source, sp, destination!.ToString(), dp);
        return true;
    }

    private void RequestSafetyStop(string reason)
    {
        if (!_isRunning || Interlocked.Exchange(ref _safetyStopRequested, 1) != 0) return;
        int generation = Volatile.Read(ref _legacyGeneration);
        _stopping = true;
        // Never join the capture worker from itself. Serialize against UI stop/start.
        _ = Task.Run(() =>
        {
            lock (_lifecycleLock)
            {
                if (_disposed || generation != _legacyGeneration) return;
                _lastCaptureError = reason;
                StopCore();
                ReconcileScope();
            }
            ErrorOccurred?.Invoke(reason);
        });
    }

    private static bool TryExtractIpAndPorts(
        IntPtr buffer, uint length,
        out System.Net.IPAddress? srcIp, out System.Net.IPAddress? dstIp,
        out ushort srcPort, out ushort dstPort)
    {
        srcIp = null;
        dstIp = null;
        srcPort = 0;
        dstPort = 0;

        if (buffer == IntPtr.Zero || length < 20) return false;

        byte b0 = Marshal.ReadByte(buffer, 0);
        int version = b0 >> 4;

        if (version == 4)
        {
            int ihl = (b0 & 0x0F) * 4;
            if (ihl < 20 || length < ihl + 8) return false;
            // Reject fragments: no reliable transport tuple on every fragment.
            if ((Marshal.ReadByte(buffer, 6) & 0x3F) != 0 || Marshal.ReadByte(buffer, 7) != 0) return false;

            byte protocol = Marshal.ReadByte(buffer, 9);
            int transportLength = protocol == 6 ? 20 : protocol == 17 ? 8 : int.MaxValue;
            int totalLength = (Marshal.ReadByte(buffer, 2) << 8) | Marshal.ReadByte(buffer, 3);
            if ((protocol != 6 && protocol != 17) || length < ihl + transportLength ||
                totalLength > length || totalLength < ihl + transportLength) return false;
            if (protocol == 17)
            {
                int udpLength = (Marshal.ReadByte(buffer, ihl + 4) << 8) | Marshal.ReadByte(buffer, ihl + 5);
                if (udpLength < 8 || udpLength > totalLength - ihl) return false;
            }

            byte[] srcBytes = new byte[4];
            byte[] dstBytes = new byte[4];
            Marshal.Copy(buffer + 12, srcBytes, 0, 4);
            Marshal.Copy(buffer + 16, dstBytes, 0, 4);
            srcIp = new System.Net.IPAddress(srcBytes);
            dstIp = new System.Net.IPAddress(dstBytes);

            if ((protocol == 6 || protocol == 17) && length >= ihl + 4)
            {
                byte s1 = Marshal.ReadByte(buffer, ihl);
                byte s2 = Marshal.ReadByte(buffer, ihl + 1);
                srcPort = (ushort)((s1 << 8) | s2);

                byte d1 = Marshal.ReadByte(buffer, ihl + 2);
                byte d2 = Marshal.ReadByte(buffer, ihl + 3);
                dstPort = (ushort)((d1 << 8) | d2);
            }
            return true;
        }
        else if (version == 6 && length >= 40)
        {
            byte nextHeader = Marshal.ReadByte(buffer, 6);
            byte[] srcBytes = new byte[16];
            byte[] dstBytes = new byte[16];
            Marshal.Copy(buffer + 8, srcBytes, 0, 16);
            Marshal.Copy(buffer + 24, dstBytes, 0, 16);
            srcIp = new System.Net.IPAddress(srcBytes);
            dstIp = new System.Net.IPAddress(dstBytes);

            if ((nextHeader == 6 || nextHeader == 17) && length >= 44)
            {
                byte s1 = Marshal.ReadByte(buffer, 40);
                byte s2 = Marshal.ReadByte(buffer, 41);
                srcPort = (ushort)((s1 << 8) | s2);

                byte d1 = Marshal.ReadByte(buffer, 42);
                byte d2 = Marshal.ReadByte(buffer, 43);
                dstPort = (ushort)((d1 << 8) | d2);
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Background loop releasing throttled packets once their Timeframe expires.
    /// Can release packets instantly (burst) or spaced out (speed control).
    /// </summary>
    private async Task ThrottleReleaseLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Bounded turns retain fairness under sustained traffic, without busy-spinning.
                for (int released = 0; released < 64 && TryReleaseNext(_timestamp()); released++) { }
                await Task.Delay(4, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Throttle release loop exception");
                RequestSafetyStop("Falha na liberação da fila: " + ex.Message);
                break;
            }
        }
    }

    private bool IsThrottleEffective => (!_armedCapture || (_stateMachine.Current == DesyncStateMachine.State.Active && _timestamp() < _effectDeadline)) &&
        (!_legacyPulseActive || WithinLegacyDeadline) && (((_activeDiagnosticMode == DiagnosticMode.OutboundFreeze ||
        _activeDiagnosticMode == DiagnosticMode.LatencyDelay || IsMinimalTimedEffect || IsBufferingEffect) &&
        _stateMachine.Current == DesyncStateMachine.State.Active) || _isThrottleActive ||
        (_throttleEnabled && (_throttleContinuous || _stateMachine.Current == DesyncStateMachine.State.Active)));

    private bool IsMinimalTimedEffect => _activeDiagnosticMode is DiagnosticMode.MicroFreeze or
        DiagnosticMode.SmoothDelay or DiagnosticMode.SoftJitter;

    private bool IsBufferingEffect => _activeDiagnosticMode is DiagnosticMode.BurstBuffering or
        DiagnosticMode.SlottedTransmission or DiagnosticMode.SawtoothDelay or
        DiagnosticMode.BandwidthShaping or DiagnosticMode.AdaptiveFreeze;

    private bool TryReleaseNext(long now)
    {
        lock (_packetLock)
        {
            ExpireArmedEffectLocked();
            if (_pulseShaper != null && _pulseShaper.TryDequeue(now, out var shaped))
            {
                SendThrottledPacket(shaped!);
                return true;
            }
            if (_scopedScheduler != null)
            {
                long before = _scopedScheduler.ReorderedPairs;
                bool ready = _scopedScheduler.TryDequeue(now, out var scopedPacket);
                Interlocked.Add(ref _reorderedPairs, _scopedScheduler.ReorderedPairs - before);
                if (ready) { SendThrottledPacket(scopedPacket!); return true; }
            }
            if (_throttleQueue.TryPeek(out var oldest))
            {
                bool effective = IsThrottleEffective;
                // A delayed worker must wait for the next open slot, not leak into closed time.
                // Pulse expiry/cancel always bypasses this gate and drains the full FIFO.
                if (effective && _activeDiagnosticMode == DiagnosticMode.SlottedTransmission &&
                    Math.Max(0, now - _effectStartedTicks) % Math.Max(1, 60L * Stopwatch.Frequency / 1000) >=
                    Math.Max(1, 10L * Stopwatch.Frequency / 1000)) return false;
                if ((!effective || (now >= oldest.ReleaseAtTicks && now >= _nextReleaseTicks)) && _throttleQueue.TryDequeue(out var packet))
                {
                    SendThrottledPacket(packet);
                    _nextReleaseTicks = _activeDiagnosticMode == DiagnosticMode.LatencyDelay || IsMinimalTimedEffect || IsBufferingEffect ? 0 :
                        Math.Max(now, _timestamp()) + (long)ThrottleReleaseDelayMs * Stopwatch.Frequency / 1000;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Flushes all throttled packets immediately to the network (used on restore or shutdown).
    /// </summary>
    public void FlushThrottleQueue()
    {
        lock (_packetLock) FlushThrottleQueueLocked();
        NotifyStats();
    }

    private void FlushThrottleQueueLocked()
    {
        if (_scopedScheduler != null)
        {
            foreach (var scoped in _scopedScheduler.Drain()) SendThrottledPacket(scoped);
            _scopedScheduler = null;
        }
        if (_pulseShaper != null)
        {
            foreach (var shaped in _pulseShaper.Drain()) SendThrottledPacket(shaped);
            _pulseShaper = null;
        }
        while (_throttleQueue.TryDequeue(out var packet))
        {
            SendThrottledPacket(packet);
        }
        _nextReleaseTicks = 0;
    }

    private bool SendPacket(IntPtr buffer, uint length, WinDivertNative.Address address)
    {
        lock (_handleLock)
        {
            if (_sendOverride != null)
                return _sendOverride(buffer, length, address);
            return _networkHandle != WinDivertNative.InvalidHandle &&
                WinDivertNative.WinDivertSend(_networkHandle, buffer, length, out var sent, ref address) && sent == length;
        }
    }

    private void RecordSend(bool success)
    {
        if (success)
            Interlocked.Increment(ref _packetsPassedThrough);
        else
        {
            Interlocked.Increment(ref _reinjectionFailures);
            Interlocked.Increment(ref _packetsDropped);
            _logger.LogWarning("Packet reinjection failed; counted as dropped, not delivered.");
            if (_isRunning) RequestSafetyStop("Reinjeção falhou; captura encerrada por segurança.");
        }
    }

    private void SendThrottledPacket(ThrottledPacket packet)
    {
        IntPtr buffer = Marshal.AllocHGlobal((int)packet.Length);
        try
        {
            Marshal.Copy(packet.Data, 0, buffer, (int)packet.Length);
            RecordSend(SendPacket(buffer, packet.Length, packet.Address));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void NotifyStats()
    {
        var subscriber = StatsUpdated;
        if (subscriber == null)
            return;

        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastStatsNotificationTicks);
        if (last != 0 && now - last < StatsIntervalTicks)
            return;
        if (Interlocked.CompareExchange(ref _lastStatsNotificationTicks, now, last) != last)
            return;

        subscriber.Invoke(
            Volatile.Read(ref _packetsIntercepted),
            Volatile.Read(ref _packetsDropped),
            Volatile.Read(ref _packetsPassedThrough),
            Volatile.Read(ref _packetsThrottled));
    }


    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _discoveryCts.Cancel();
            StopCore();
            _flowTracker.Dispose();
            _targetProcess?.Dispose(); _targetProcess = null; _targetProcessId = 0;
            PublishEmpty();
        }
        // Discovery may be waiting for lifecycle lock; join only after releasing it.
        _discoveryTask?.GetAwaiter().GetResult();
        _discoveryCts.Dispose();
        _scopeSignal.Dispose();
    }
}
