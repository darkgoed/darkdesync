using System.Diagnostics;
using DarkDesync.Core;
using DarkDesync.Models;
using Microsoft.Extensions.Logging;

namespace DarkDesync.Services;

/// <summary>
/// High-level controller managing desync cycle execution, precise async countdowns,
/// state transitions (IDLE -> ACTIVE -> RESTORING -> IDLE), and traffic restoration safety.
/// </summary>
public sealed class DesyncController : IDisposable
{
    private readonly ILogger<DesyncController> _logger;
    private readonly DesyncStateMachine _stateMachine;
    private readonly PacketEngine? _packetEngine; // Nullable for headless testing
    private readonly object _cycleLock = new();

    private CancellationTokenSource? _activeCycleCts;
    private Task? _activeCycleTask;

    public DesyncStateMachine StateMachine => _stateMachine;
    public DesyncStateMachine.State CurrentState => _stateMachine.Current;

    // Active profile currently selected
    public IntensityProfile ActiveProfile { get; set; } = IntensityProfile.Weak;

    // Target process metadata
    public ProcessInfo? TargetProcess { get; set; }

    // Events for UI notification
    public event Action<DesyncStateMachine.State>? StateChanged;
    public event Action<int, int>? CountdownTick; // remainingMs, totalMs
    public event Action<CycleRecord>? CycleFinished;
    public event Action<string>? LogMessage;

    public DesyncController(
        ILogger<DesyncController> logger,
        DesyncStateMachine stateMachine,
        PacketEngine? packetEngine = null)
    {
        _logger = logger;
        _stateMachine = stateMachine;
        _packetEngine = packetEngine;

        _stateMachine.StateChanged += (oldState, newState) =>
        {
            _logger.LogInformation("Controller State: {Old} -> {New}", oldState, newState);
            StateChanged?.Invoke(newState);
        };
    }

    /// <summary>
    /// Attempts to trigger a desync cycle.
    /// Returns true if cycle started; false if ignored (e.g., already in cycle or invalid target).
    /// </summary>
    public bool TriggerCycle()
    {
        lock (_cycleLock)
        {
            // Re-entrancy protection: If not IDLE, ignore trigger immediately.
            if (_stateMachine.Current != DesyncStateMachine.State.Idle)
            {
                _logger.LogDebug("TriggerCycle ignored: state is {State}", _stateMachine.Current);
                return false;
            }

            if (!_stateMachine.TryActivate())
            {
                _logger.LogDebug("TriggerCycle failed to transition to Active");
                return false;
            }

            var profile = ActiveProfile;
            var target = TargetProcess;

            _activeCycleCts?.Cancel();
            _activeCycleCts?.Dispose();
            _activeCycleCts = new CancellationTokenSource();
            var ct = _activeCycleCts.Token;

            _logger.LogInformation("Starting Desync cycle: Profile={Profile}, Duration={Duration}ms, Target={Target}",
                profile.Name, profile.DurationMs, target?.ProcessName ?? "All");
            LogMessage?.Invoke($"[START] Ciclo {profile.Name} iniciado ({profile.DurationMs}ms)");

            _activeCycleTask = Task.Run(() => ExecuteCycleAsync(profile, target, ct), ct);
            return true;
        }
    }

    /// <summary>
    /// Cancels any active cycle and safely restores traffic immediately.
    /// </summary>
    public void CancelCycle()
    {
        lock (_cycleLock)
        {
            if (_stateMachine.Current == DesyncStateMachine.State.Idle)
                return;

            _logger.LogInformation("Cancelling active desync cycle");
            _activeCycleCts?.Cancel();

            try
            {
                _activeCycleTask?.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch { }

            EnsureRestored();
        }
    }

    private async Task ExecuteCycleAsync(IntensityProfile profile, ProcessInfo? target, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        long packetsStart = _packetEngine?.PacketsIntercepted ?? 0;
        long droppedStart = _packetEngine?.PacketsDropped ?? 0;
        bool completedNormally = false;

        var stopwatch = Stopwatch.StartNew();
        int totalMs = profile.DurationMs;

        try
        {
            // UI countdown at 20 Hz; Stopwatch still controls the actual deadline.
            while (stopwatch.ElapsedMilliseconds < totalMs)
            {
                ct.ThrowIfCancellationRequested();

                int elapsed = (int)stopwatch.ElapsedMilliseconds;
                int remaining = Math.Max(0, totalMs - elapsed);
                CountdownTick?.Invoke(remaining, totalMs);

                int delay = Math.Min(50, remaining);
                if (delay <= 0) break;
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            CountdownTick?.Invoke(0, totalMs);
            completedNormally = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cycle was cancelled prematurely");
            LogMessage?.Invoke("[ABORT] Ciclo interrompido prematuramente");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected exception during cycle countdown");
            LogMessage?.Invoke($"[ERROR] Falha durante ciclo: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            var endTime = DateTime.UtcNow;

            // Transition: ACTIVE -> RESTORING
            if (_stateMachine.TryBeginRestore())
            {
                _logger.LogDebug("Traffic restoration in progress...");
                LogMessage?.Invoke("[RESTORING] Restaurando tráfego de rede...");

                // Allow brief settling period for in-flight packets
                await Task.Delay(40).ConfigureAwait(false);

                // Transition: RESTORING -> IDLE
                _stateMachine.TryComplete();
                LogMessage?.Invoke("[IDLE] Tráfego normal restabelecido");
            }
            else
            {
                EnsureRestored();
            }

            long packetsEnd = _packetEngine?.PacketsIntercepted ?? 0;
            long droppedEnd = _packetEngine?.PacketsDropped ?? 0;

            var record = new CycleRecord
            {
                StartTime = startTime.ToLocalTime(),
                EndTime = endTime.ToLocalTime(),
                ProfileName = profile.Name,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                DropPercentage = profile.DropPercentage,
                PacketsDropped = droppedEnd - droppedStart,
                PacketsTotal = packetsEnd - packetsStart,
                TargetProcess = target?.ProcessName ?? "Global",
                CompletedNormally = completedNormally
            };

            CycleFinished?.Invoke(record);
        }
    }

    /// <summary>
    /// Triggers a diagnostic cycle (100–200 ms, default 150 ms) for the specified diagnostic mode.
    /// Guarantees automatic restoration of network traffic upon completion or cancellation.
    /// </summary>
    public bool TriggerDiagnosticCycle(DiagnosticMode mode, int durationMs = 150)
    {
        lock (_cycleLock)
        {
            if (_stateMachine.Current != DesyncStateMachine.State.Idle)
            {
                _logger.LogDebug("TriggerDiagnosticCycle ignored: state is {State}", _stateMachine.Current);
                return false;
            }

            if (!_stateMachine.TryActivate())
            {
                _logger.LogDebug("TriggerDiagnosticCycle failed to transition to Active");
                return false;
            }

            int clampedDuration = Math.Clamp(durationMs, 0, 800);
            if (_packetEngine != null)
            {
                _packetEngine.ActiveDiagnosticMode = mode;
            }

            string name = mode switch
            {
                DiagnosticMode.OutboundFreeze => "Outbound Freeze",
                DiagnosticMode.BurstLoss => "Burst Loss",
                DiagnosticMode.AsymmetricLoss => "Asymmetric Loss",
                _ => "Diagnóstico"
            };

            int dropPct = mode switch
            {
                DiagnosticMode.BurstLoss => _packetEngine?.BurstLossPercentage ?? 50,
                DiagnosticMode.AsymmetricLoss => _packetEngine?.AsymmetricOutboundLossPercentage ?? 75,
                _ => 100
            };

            var profile = new IntensityProfile
            {
                Name = name,
                DurationMs = clampedDuration,
                DropPercentage = dropPct,
                Color = mode switch
                {
                    DiagnosticMode.OutboundFreeze => "#5EEAD4",
                    DiagnosticMode.BurstLoss => "#F4C78A",
                    DiagnosticMode.AsymmetricLoss => "#FB7185",
                    _ => "#A78BFA"
                }
            };

            var target = TargetProcess;

            _activeCycleCts?.Cancel();
            _activeCycleCts?.Dispose();
            _activeCycleCts = new CancellationTokenSource();
            var ct = _activeCycleCts.Token;

            _logger.LogInformation("Starting Diagnostic cycle: Mode={Mode}, Duration={Duration}ms, Target={Target}",
                mode, clampedDuration, target?.ProcessName ?? "Global");
            LogMessage?.Invoke($"[DIAG] Ciclo {profile.Name} iniciado ({clampedDuration}ms)");

            _activeCycleTask = Task.Run(() => ExecuteDiagnosticCycleAsync(profile, mode, target, ct), ct);
            return true;
        }
    }

    private async Task ExecuteDiagnosticCycleAsync(IntensityProfile profile, DiagnosticMode mode, ProcessInfo? target, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        long packetsStart = _packetEngine?.PacketsIntercepted ?? 0;
        long droppedStart = _packetEngine?.PacketsDropped ?? 0;
        bool completedNormally = false;

        var stopwatch = Stopwatch.StartNew();
        int totalMs = profile.DurationMs;

        try
        {
            while (stopwatch.ElapsedMilliseconds < totalMs)
            {
                ct.ThrowIfCancellationRequested();

                int elapsed = (int)stopwatch.ElapsedMilliseconds;
                int remaining = Math.Max(0, totalMs - elapsed);
                CountdownTick?.Invoke(remaining, totalMs);

                int delay = Math.Min(20, remaining);
                if (delay <= 0) break;
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            CountdownTick?.Invoke(0, totalMs);
            completedNormally = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Diagnostic cycle was cancelled prematurely");
            LogMessage?.Invoke("[ABORT] Ciclo de diagnóstico cancelado prematuramente");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected exception during diagnostic cycle");
            LogMessage?.Invoke($"[ERROR] Falha durante diagnóstico: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            var endTime = DateTime.UtcNow;

            // Reset engine diagnostic mode
            if (_packetEngine != null)
            {
                _packetEngine.ActiveDiagnosticMode = DiagnosticMode.None;
            }

            // Automatic Restoration: ACTIVE -> RESTORING -> IDLE
            if (_stateMachine.TryBeginRestore())
            {
                _logger.LogDebug("Diagnostic traffic restoration in progress...");
                LogMessage?.Invoke("[RESTORING] Restaurando tráfego de diagnóstico...");

                _packetEngine?.FlushThrottleQueue();
                await Task.Delay(20).ConfigureAwait(false);

                _stateMachine.TryComplete();
                LogMessage?.Invoke("[IDLE] Tráfego normal restabelecido automaticamente");
            }
            else
            {
                EnsureRestored();
            }

            long packetsEnd = _packetEngine?.PacketsIntercepted ?? 0;
            long droppedEnd = _packetEngine?.PacketsDropped ?? 0;

            var record = new CycleRecord
            {
                StartTime = startTime.ToLocalTime(),
                EndTime = endTime.ToLocalTime(),
                ProfileName = profile.Name,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                DropPercentage = profile.DropPercentage,
                PacketsDropped = droppedEnd - droppedStart,
                PacketsTotal = packetsEnd - packetsStart,
                TargetProcess = target?.ProcessName ?? "Global",
                CompletedNormally = completedNormally
            };

            CycleFinished?.Invoke(record);
        }
    }

    private void EnsureRestored()
    {
        if (_packetEngine != null)
        {
            _packetEngine.ActiveDiagnosticMode = DiagnosticMode.None;
            _packetEngine.FlushThrottleQueue();
        }
        _stateMachine.ForceIdle();
        LogMessage?.Invoke("[SAFETY] Restauração forçada de tráfego acionada");
    }

    public void Dispose()
    {
        CancelCycle();
        _activeCycleCts?.Dispose();
    }
}
