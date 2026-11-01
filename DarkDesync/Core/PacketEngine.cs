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
    public long ReleaseAtTicks { get; init; }
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


    private uint _targetProcessId;
    private Process? _targetProcess;
    private ProcessNetworkScanner.ProcessEndpoint[] _scopedConnections = [];
    internal Func<bool>? TargetAliveOverride { get; set; }
    internal Func<uint, List<ProcessNetworkScanner.ProcessEndpoint>>? EndpointsOverride { get; set; }
    private List<ProcessNetworkScanner.ProcessEndpoint> ReadEndpoints() =>
        EndpointsOverride?.Invoke(_targetProcessId) ?? _scanner.GetEndpointsForProcess(_targetProcessId);
    private FilterMode _filterMode = FilterMode.Outbound;
    private string _customFilter = "outbound";
    private int _dropPercentage = 100;
    private volatile bool _isRunning;
    private string _currentFilter = "outbound";

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
        set => _activeDiagnosticMode = value;
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

    private volatile bool _freezeDropsPackets = false;
    public bool FreezeDropsPackets
    {
        get => _freezeDropsPackets;
        set => _freezeDropsPackets = value;
    }

    private readonly ConcurrentQueue<ThrottledPacket> _throttleQueue = new();
    private readonly object _handleLock = new();
    // Always acquire packet lock before handle lock: enqueue, release and flush are atomic.
    private readonly object _packetLock = new();
    private long _nextReleaseTicks;
    private bool _stopping;
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
    public bool IsRunning => _isRunning;
    public string CurrentFilter => _currentFilter;
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
        if (_isRunning)
        {
            _logger.LogInformation("PacketEngine is already running. Updating filter...");
            UpdateFilter(mode, processId, customFilter);
            return true;
        }

        _filterMode = mode;
        _targetProcessId = processId;
        _dropPercentage = Math.Clamp(dropPercentage, 0, 100);
        _customFilter = customFilter ?? "outbound";

        // Reset stats
        Interlocked.Exchange(ref _packetsIntercepted, 0);
        Interlocked.Exchange(ref _packetsDropped, 0);
        Interlocked.Exchange(ref _packetsPassedThrough, 0);
        Interlocked.Exchange(ref _packetsThrottled, 0);
        Interlocked.Exchange(ref _lastStatsNotificationTicks, 0);

        _currentFilter = ResolveFilterString();

        if (_currentFilter == "false")
        {
            ErrorOccurred?.Invoke("Captura bloqueada: selecione um processo com conexão TCP/IPv4 estabelecida e exclusiva. UDP, IPv6 e portas isoladas não identificam o alvo com segurança nesta versão.");
            return false;
        }

        if (!OpenNetworkHandle(_currentFilter))
        {
            return false;
        }

        _cts = new CancellationTokenSource();
        lock (_packetLock)
        {
            _stopping = false;
            _nextReleaseTicks = 0;
            _isRunning = true;
        }

        _captureTask = Task.Run(() => CaptureLoop(_cts.Token));
        _throttleReleaseTask = Task.Run(() => ThrottleReleaseLoop(_cts.Token));

        // Connection scope is immutable for this capture session. New connections
        // require an explicit restart, never a broad port-based fallback.

        _logger.LogInformation("PacketEngine started. Filter: '{Filter}', Drop: {Drop}%, Throttle: {Throttle}",
            _currentFilter, _dropPercentage, _throttleEnabled);
        return true;
    }

    public bool Start(uint processId, int dropPercentage = 100)
    {
        return Start(FilterMode.Outbound, processId, dropPercentage);
    }

    public void UpdateFilter(FilterMode mode, uint processId = 0, string? customFilter = null)
    {
        // Stop and flush before any target/filter change; never close a live sender.
        Stop();
        _filterMode = mode;
        _targetProcessId = processId;
        if (!string.IsNullOrWhiteSpace(customFilter))
            _customFilter = customFilter;
        _currentFilter = "false";
    }

    private string ResolveFilterString()
    {
        _scopedConnections = [];
        _targetProcess?.Dispose();
        _targetProcess = null;
        if (_targetProcessId == 0) return "false";
        try
        {
            _targetProcess = Process.GetProcessById((int)_targetProcessId);
            if (_targetProcess.HasExited) return "false";
            _ = _targetProcess.Handle; // Bind identity to this process, not a reusable PID.
            _scopedConnections = ReadEndpoints().Where(e => e.Protocol == 6 && e.LocalPort != 0 &&
                e.RemotePort != 0 && e.LocalAddr != 0 && e.RemoteAddr != 0).Distinct().ToArray();
            string restriction = _filterMode == FilterMode.ProcessPorts ? "true" :
                _filterBuilder.GetFilter(_filterMode, null, _customFilter);
            if (restriction == "false") return "false";
            return FilterBuilder.BuildScopedFilter(_scopedConnections, restriction);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Target scope unavailable");
            return "false";
        }
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _logger.LogInformation("Stopping PacketEngine");
        lock (_packetLock)
        {
            _stopping = true;
            _isRunning = false;
        }

        _flowTracker.Stop();

        _cts?.Cancel();

        lock (_handleLock)
        {
            if (_networkHandle != WinDivertNative.InvalidHandle)
            {
                WinDivertNative.WinDivertShutdown(_networkHandle, WinDivertNative.Shutdown.Recv);
            }
        }

        try
        {
            _captureTask?.GetAwaiter().GetResult();
            _throttleReleaseTask?.GetAwaiter().GetResult();
        }
        catch { }

        // Keep sending available until workers and the final queue flush finish.
        lock (_packetLock)
        {
            FlushThrottleQueueLocked();
            CloseNetworkHandle();
        }
        NotifyStats();

        _cts?.Dispose();
        _cts = null;

        _stateMachine.ForceIdle();
        ActiveDiagnosticMode = DiagnosticMode.None;
        _targetProcess?.Dispose();
        _targetProcess = null;
        _scopedConnections = [];
        _logger.LogInformation("PacketEngine stopped safely.");
    }

    public void SetDropPercentage(int percentage)
    {
        _dropPercentage = Math.Clamp(percentage, 0, 100);
    }

    private bool OpenNetworkHandle(string filter)
    {
        lock (_handleLock)
        {
            _networkHandle = WinDivertNative.WinDivertOpen(
                filter,
                WinDivertNative.Layer.Network,
                (short)0,
                0);

            if (_networkHandle == WinDivertNative.InvalidHandle)
            {
                int err = Marshal.GetLastWin32Error();
                _logger.LogError("WinDivertOpen failed for filter '{Filter}': Win32 error {Error}", filter, err);

                string errorMsg = err switch
                {
                    2 => "Arquivos do driver WinDivert não encontrados (WinDivert.dll / WinDivert64.sys).",
                    5 => "Acesso negado. O DarkDesync precisa ser executado como Administrador para carregar o driver de rede.",
                    87 => $"Filtro WinDivert inválido: '{filter}'",
                    577 => "O driver WinDivert não possui assinatura válida no sistema.",
                    _ => $"Erro WinDivert #{err}"
                };

                ErrorOccurred?.Invoke(errorMsg);
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
    private void CaptureLoop(CancellationToken ct)
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
                IntPtr currentHandle;
                lock (_handleLock)
                {
                    currentHandle = _networkHandle;
                }

                if (currentHandle == WinDivertNative.InvalidHandle)
                {
                    if (ct.IsCancellationRequested) break;
                    Thread.Sleep(20);
                    continue;
                }

                var addr = new WinDivertNative.Address();
                bool success = WinDivertNative.WinDivertRecv(
                    currentHandle, packetBuffer, (uint)bufferSize, out uint recvLen, ref addr);

                if (!success)
                {
                    if (ct.IsCancellationRequested || !_isRunning)
                        break;

                    int err = Marshal.GetLastWin32Error();
                    if (err == 995 || err == 6)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    Thread.Sleep(1);
                    continue;
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
            Interlocked.Increment(ref _packetsIntercepted);
            // A Recv finishing during Stop must not overtake packets already held.
            if (_stopping) FlushThrottleQueueLocked();

            bool active = _stateMachine.Current == DesyncStateMachine.State.Active;

            // All effects, including legacy drop/throttle, require live ownership.
            // Driver-free scheduling tests use SendOverride without starting capture.
            if ((_isRunning || _activeDiagnosticMode != DiagnosticMode.None) &&
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
                    case DiagnosticMode.OutboundFreeze:
                    {
                        if (addr.Outbound)
                        {
                            if (_freezeDropsPackets)
                            {
                                Interlocked.Increment(ref _packetsDropped);
                                return true;
                            }
                            else
                            {
                                // Bound retained memory even under a saturated sender.
                                if (_throttleQueue.Count >= 512 || recvLen > WinDivertNative.MtuMax)
                                {
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

                    case DiagnosticMode.BurstLoss:
                    {
                        int pct = Math.Clamp(_burstLossPercentage, 0, 100);
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
                if (_throttleQueue.Count >= 512 || recvLen > WinDivertNative.MtuMax)
                {
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
    /// Verifies whether the packet matches authorized test criteria (loopback or target process ports).
    /// </summary>
    public bool IsAuthorizedTraffic(IntPtr packetBuffer, uint recvLen, WinDivertNative.Address addr)
    {
        if (!TryExtractIpAndPorts(packetBuffer, recvLen, out var srcIp, out var dstIp, out var srcPort, out var dstPort))
        {
            // If headers cannot be parsed, pass through safely without drop
            return false;
        }

        if (Marshal.ReadByte(packetBuffer, 0) >> 4 != 4 || Marshal.ReadByte(packetBuffer, 9) != 6)
            return false;
        try
        {
            if (!(TargetAliveOverride?.Invoke() ?? (_targetProcess != null && !_targetProcess.HasExited))) return false;
            var localIp = addr.Outbound ? srcIp : dstIp;
            var remoteIp = addr.Outbound ? dstIp : srcIp;
            ushort localPort = addr.Outbound ? srcPort : dstPort;
            ushort remotePort = addr.Outbound ? dstPort : srcPort;
            // Revalidate the full tuple, not either port or an arbitrary loopback packet.
            return ReadEndpoints().Any(e => e.Protocol == 6 && e.LocalPort == localPort &&
                e.RemotePort == remotePort && new System.Net.IPAddress(e.LocalAddr).Equals(localIp) &&
                new System.Net.IPAddress(e.RemoteAddr).Equals(remoteIp));
        }
        catch { return false; }
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
            if (ihl < 20 || length < ihl + 20) return false;
            // Reject fragments: no reliable transport tuple on every fragment.
            if ((Marshal.ReadByte(buffer, 6) & 0x3F) != 0 || Marshal.ReadByte(buffer, 7) != 0) return false;

            byte protocol = Marshal.ReadByte(buffer, 9);

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
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                if (TryReleaseNext(_timestamp()))
                {
                    continue;
                }

                await Task.Delay(4, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Throttle release loop exception");
            }
        }
    }

    private bool IsThrottleEffective => (_activeDiagnosticMode == DiagnosticMode.OutboundFreeze &&
        _stateMachine.Current == DesyncStateMachine.State.Active) || _isThrottleActive ||
        (_throttleEnabled && (_throttleContinuous || _stateMachine.Current == DesyncStateMachine.State.Active));

    private bool TryReleaseNext(long now)
    {
        lock (_packetLock)
        {
            if (_throttleQueue.TryPeek(out var oldest))
            {
                bool effective = IsThrottleEffective;
                if ((!effective || (now >= oldest.ReleaseAtTicks && now >= _nextReleaseTicks)) && _throttleQueue.TryDequeue(out var packet))
                {
                    SendThrottledPacket(packet);
                    _nextReleaseTicks = Math.Max(now, _timestamp()) + (long)ThrottleReleaseDelayMs * Stopwatch.Frequency / 1000;
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
            Interlocked.Increment(ref _packetsDropped);
            _logger.LogWarning("Packet reinjection failed; counted as dropped, not delivered.");
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
        Stop();
        _flowTracker.Dispose();
    }
}
