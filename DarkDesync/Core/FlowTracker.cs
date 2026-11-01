using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DarkDesync.Core;

/// <summary>
/// Tracks network flows for a target process using WinDivert's FLOW layer.
/// This provides real-time awareness of new connections being established,
/// complementing the periodic port scanning from ProcessNetworkScanner.
/// 
/// Uses WinDivert FLOW layer with SNIFF + RECV_ONLY flags to observe
/// flow events without intercepting actual packets.
/// </summary>
public sealed class FlowTracker : IDisposable
{
    private readonly ILogger<FlowTracker> _logger;
    private IntPtr _flowHandle = WinDivertNative.InvalidHandle;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private uint _targetProcessId;

    /// <summary>
    /// Thread-safe set of local ports currently in use by the target process.
    /// </summary>
    private readonly ConcurrentDictionary<ushort, byte> _activePorts = new();

    /// <summary>
    /// Raised when the set of active ports changes (new flow or flow deleted).
    /// </summary>
    public event Action? PortsChanged;

    public FlowTracker(ILogger<FlowTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a snapshot of all currently tracked local ports.
    /// </summary>
    public HashSet<ushort> GetActivePorts()
    {
        return new HashSet<ushort>(_activePorts.Keys);
    }

    /// <summary>
    /// Starts tracking flows for the specified process.
    /// </summary>
    public bool Start(uint processId)
    {
        if (_flowHandle != WinDivertNative.InvalidHandle)
        {
            _logger.LogWarning("FlowTracker already running. Stop it first.");
            return false;
        }

        _targetProcessId = processId;
        _activePorts.Clear();

        // Open FLOW layer handle to monitor socket events for target process
        // SNIFF flag: don't interfere with flows, just observe
        // RECV_ONLY flag: we only read events, never inject
        string filter = "true"; // Capture all flows, filter by PID in code
        _flowHandle = WinDivertNative.WinDivertOpen(
            filter,
            WinDivertNative.Layer.Flow,
            (short)0,
            WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly);

        if (_flowHandle == WinDivertNative.InvalidHandle)
        {
            int err = Marshal.GetLastWin32Error();
            _logger.LogError("Failed to open WinDivert FLOW handle: Win32 error {Error}", err);
            return false;
        }

        _logger.LogInformation("FlowTracker started for PID {Pid}", processId);

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorFlows(_cts.Token));

        return true;
    }

    /// <summary>
    /// Stops tracking flows.
    /// </summary>
    public void Stop()
    {
        if (_flowHandle == WinDivertNative.InvalidHandle)
            return;

        _logger.LogInformation("Stopping FlowTracker");

        _cts?.Cancel();

        // Shutdown the handle to unblock WinDivertRecv
        if (_flowHandle != WinDivertNative.InvalidHandle)
        {
            WinDivertNative.WinDivertShutdown(_flowHandle, WinDivertNative.Shutdown.Both);
        }

        try
        {
            _monitorTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException) { }

        if (_flowHandle != WinDivertNative.InvalidHandle)
        {
            WinDivertNative.WinDivertClose(_flowHandle);
            _flowHandle = WinDivertNative.InvalidHandle;
        }

        _cts?.Dispose();
        _cts = null;
        _activePorts.Clear();

        _logger.LogInformation("FlowTracker stopped");
    }

    private void MonitorFlows(CancellationToken ct)
    {
        // Flow layer doesn't have packet data, just address info
        var addr = new WinDivertNative.Address();

        while (!ct.IsCancellationRequested)
        {
            // For FLOW layer, pPacket is NULL and packetLen is 0
            bool success = WinDivertNative.WinDivertRecv(
                _flowHandle, IntPtr.Zero, 0, out _, ref addr);

            if (!success)
            {
                if (ct.IsCancellationRequested)
                    break;

                int err = Marshal.GetLastWin32Error();
                if (err == 995) // ERROR_OPERATION_ABORTED - handle was shutdown
                    break;

                _logger.LogWarning("WinDivertRecv (FLOW) failed: error {Error}", err);
                continue;
            }

            var evt = (WinDivertNative.Event)addr.EventValue;
            var flow = addr.Flow;

            // Only track flows for our target process
            if (flow.ProcessId != _targetProcessId)
                continue;

            ushort localPort = flow.LocalPort;
            if (localPort == 0)
                continue;

            switch (evt)
            {
                case WinDivertNative.Event.FlowEstablished:
                    if (_activePorts.TryAdd(localPort, 0))
                    {
                        _logger.LogDebug("Flow established: PID {Pid} port {Port} protocol {Proto}",
                            flow.ProcessId, localPort, flow.Protocol);
                        PortsChanged?.Invoke();
                    }
                    break;

                case WinDivertNative.Event.FlowDeleted:
                    if (_activePorts.TryRemove(localPort, out _))
                    {
                        _logger.LogDebug("Flow deleted: PID {Pid} port {Port}", flow.ProcessId, localPort);
                        PortsChanged?.Invoke();
                    }
                    break;
            }
        }

        _logger.LogDebug("Flow monitor loop exited");
    }

    public void Dispose()
    {
        Stop();
    }
}
