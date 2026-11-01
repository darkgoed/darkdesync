using System.Diagnostics;
using DarkDesync.Core;
using DarkDesync.Models;
using Microsoft.Extensions.Logging;

namespace DarkDesync.Services;

/// <summary>One serialized, timed cycle for every effect.</summary>
public sealed class DesyncController : IDisposable
{
    private readonly ILogger<DesyncController> _logger;
    private readonly DesyncStateMachine _stateMachine;
    private readonly PacketEngine? _packetEngine;
    private readonly object _cycleLock = new();
    private CancellationTokenSource? _activeCycleCts;
    private Task? _activeCycleTask;
    private bool _disposed;
    public DesyncStateMachine StateMachine => _stateMachine;
    public DesyncStateMachine.State CurrentState => _stateMachine.Current;
    public IntensityProfile ActiveProfile { get; set; } = IntensityProfile.Weak;
    public ProcessInfo? TargetProcess { get; set; }
    public event Action<DesyncStateMachine.State>? StateChanged;
    public event Action<int, int>? CountdownTick;
    public event Action<CycleRecord>? CycleFinished;
    public event Action<string>? LogMessage;

    public DesyncController(ILogger<DesyncController> logger, DesyncStateMachine stateMachine, PacketEngine? packetEngine = null)
    {
        _logger = logger;
        _stateMachine = stateMachine;
        _packetEngine = packetEngine;
        _stateMachine.StateChanged += (_, state) => StateChanged?.Invoke(state);
    }

    public bool TriggerCycle(DiagnosticMode mode = DiagnosticMode.None, int? durationMs = null,
        bool legacyGlobal = false, bool globalConsent = false)
    {
        lock (_cycleLock)
        {
            if (_disposed || !Enum.IsDefined(mode) || CurrentState != DesyncStateMachine.State.Idle ||
                _activeCycleTask is { IsCompleted: false }) return false;
            if (legacyGlobal && (!globalConsent || _packetEngine == null))
            {
                LogMessage?.Invoke("[GLOBAL BLOQUEADO] Consentimento explícito obrigatório; nenhum handle aberto.");
                return false;
            }
            var profile = new IntensityProfile
            {
                Name = mode switch
                {
                    DiagnosticMode.OutboundFreeze => "Outbound Freeze",
                    DiagnosticMode.BurstLoss => "Burst Loss",
                    DiagnosticMode.AsymmetricLoss => "Asymmetric Loss",
                    _ => ActiveProfile.Name
                },
                DurationMs = mode == DiagnosticMode.None && !legacyGlobal ? ActiveProfile.DurationMs : Math.Clamp(durationMs ?? 150, 0, 800),
                DropPercentage = ActiveProfile.DropPercentage
            };
            if (profile.DurationMs <= 0) return false;
            if (_packetEngine != null) _packetEngine.ActiveDiagnosticMode = mode;
            if (!_stateMachine.TryActivate()) return false;
            if (legacyGlobal)
            {
                try
                {
                    _packetEngine!.SetDropPercentage(profile.DropPercentage);
                    if (!_packetEngine.BeginLegacyGlobalPulse(profile.DurationMs, globalConsent))
                    { RestoreTraffic(); return false; }
                }
                catch (Exception ex)
                {
                    RestoreTraffic();
                    LogMessage?.Invoke("[GLOBAL ERRO] Tráfego restaurado: " + ex.Message);
                    return false;
                }
            }
            _activeCycleCts?.Dispose();
            _activeCycleCts = new CancellationTokenSource();
            var ct = _activeCycleCts.Token;
            var target = TargetProcess;
            try
            {
                LogMessage?.Invoke($"[START] {(legacyGlobal ? "GLOBAL todos os aplicativos" : "PID estrito")} • {profile.Name} • duração solicitada {profile.DurationMs} ms");
                // Restoration must execute even if cancelled before scheduling.
                _activeCycleTask = Task.Run(() => ExecuteCycleAsync(profile, target, ct, legacyGlobal));
            }
            catch
            {
                _activeCycleCts.Cancel();
                RestoreTraffic();
                return false;
            }
            return true;
        }
    }

    public bool TriggerDiagnosticCycle(DiagnosticMode mode, int durationMs = 150) => TriggerCycle(mode, durationMs);

    public void CancelCycle()
    {
        Task? task;
        lock (_cycleLock)
        {
            _activeCycleCts?.Cancel();
            RestoreTraffic();
            task = _activeCycleTask;
        }
        // UI event handlers dispatch asynchronously; do not let an old worker
        // restore the engine after a new pulse has begun.
        task?.GetAwaiter().GetResult();
    }

    private void RestoreTraffic()
    {
        // Leave Active before clearing the mode, so legacy drop cannot leak in.
        _stateMachine.TryBeginRestore();
        if (_packetEngine != null)
        {
            _packetEngine.ActiveDiagnosticMode = DiagnosticMode.None;
            _packetEngine.FlushThrottleQueue();
            _packetEngine.EndLegacyGlobalPulse(); // Close global handle on success, cancel and every exception.
        }
        _stateMachine.ForceIdle();
    }

    private async Task ExecuteCycleAsync(IntensityProfile profile, ProcessInfo? target, CancellationToken ct, bool legacyGlobal)
    {
        var start = DateTime.Now;
        long packets = _packetEngine?.PacketsIntercepted ?? 0;
        long dropped = _packetEngine?.PacketsDropped ?? 0;
        bool completed = false;
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.ElapsedMilliseconds < profile.DurationMs)
            {
                ct.ThrowIfCancellationRequested();
                int remaining = Math.Max(0, profile.DurationMs - (int)clock.ElapsedMilliseconds);
                CountdownTick?.Invoke(remaining, profile.DurationMs);
                await Task.Delay(Math.Min(50, remaining), ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            CountdownTick?.Invoke(0, profile.DurationMs);
            completed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cycle failed");
            LogMessage?.Invoke($"[ERROR] {ex.Message}");
        }
        finally
        {
            clock.Stop();
            RestoreTraffic();
            CycleFinished?.Invoke(new CycleRecord
            {
                StartTime = start, EndTime = DateTime.Now, ProfileName = profile.Name,
                DurationMs = (int)clock.ElapsedMilliseconds, DropPercentage = profile.DropPercentage,
                PacketsDropped = (_packetEngine?.PacketsDropped ?? 0) - dropped,
                PacketsTotal = (_packetEngine?.PacketsIntercepted ?? 0) - packets,
                TargetProcess = legacyGlobal ? "GLOBAL — todos os aplicativos" : target?.ProcessName ?? "Nenhum", CompletedNormally = completed
            });
            LogMessage?.Invoke($"[FIM] {(legacyGlobal ? "GLOBAL" : "PID estrito")} • duração medida {clock.ElapsedMilliseconds} ms • captura iniciada não comprova canal de jogo/efeito no Roblox.");
        }
    }

    public void Dispose()
    {
        lock (_cycleLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { CancelCycle(); }
        finally
        {
            lock (_cycleLock)
            {
                _activeCycleCts?.Dispose();
                _activeCycleCts = null;
            }
        }
    }
}
