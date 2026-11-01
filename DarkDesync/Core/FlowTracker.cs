using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DarkDesync.Core;

/// <summary>
/// Observes only the target PID's IPv4 UDP FLOW metadata; never intercepts payload.
/// A UDP binding/port alone is NOT a flow identity. Events preceding Start are unknown.
/// </summary>
public sealed class FlowTracker : IDisposable
{
    private readonly ILogger<FlowTracker> _logger;
    private readonly object _lifecycleLock = new();
    private IntPtr _handle = WinDivertNative.InvalidHandle;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private UdpFlowLedger? _ledger;
    private volatile bool _healthy;
    public event Action<string>? Failed;
    public bool IsHealthy => _healthy && _process is { HasExited: false };
    internal static string ObservationFilter(uint pid) =>
        $"processId == {pid} and protocol == 17 and not ipv6";

    public FlowTracker(ILogger<FlowTracker> logger) => _logger = logger;

    public bool Start(uint pid)
    {
        lock (_lifecycleLock)
        {
            if (pid != 0 && IsHealthy && _ledger?.ProcessId == pid) return true;
            StopCore();
            if (pid == 0) return false;
            try
            {
                _process = Process.GetProcessById((int)pid);
                _ = _process.Handle; // Pin this process identity against PID reuse.
                if (_process.HasExited) { StopCore(); return false; }
                _handle = WinDivertNative.WinDivertOpen(ObservationFilter(pid), WinDivertNative.Layer.Flow, 0,
                    WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly);
                if (_handle == WinDivertNative.InvalidHandle)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "UDP FLOW observation unavailable");
                _ledger = new UdpFlowLedger(pid);
                _healthy = true;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                var handle = _handle;
                var ledger = _ledger;
                _worker = Task.Run(() => Monitor(handle, ledger, token));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UDP flow observation blocked for PID {Pid}", pid);
                StopCore();
                return false;
            }
        }
    }

    internal ProcessNetworkScanner.ProcessEndpoint[] GetUdpFlows() =>
        IsHealthy && _ledger is { } ledger ? ledger.Snapshot() : [];

    public void Stop() { lock (_lifecycleLock) StopCore(); }
    private void StopCore()
    {
        _healthy = false;
        _cts?.Cancel();
        if (_handle != WinDivertNative.InvalidHandle)
            WinDivertNative.WinDivertShutdown(_handle, WinDivertNative.Shutdown.Both);
        _worker?.GetAwaiter().GetResult();
        if (_handle != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertClose(_handle);
        _handle = WinDivertNative.InvalidHandle;
        _cts?.Dispose(); _cts = null; _worker = null;
        _process?.Dispose(); _process = null;
        _ledger = null;
    }

    private void Monitor(IntPtr handle, UdpFlowLedger ledger, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var addr = new WinDivertNative.Address();
                if (!WinDivertNative.WinDivertRecv(handle, IntPtr.Zero, 0, out _, ref addr))
                {
                    if (ct.IsCancellationRequested) return;
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "UDP FLOW receive failed");
                }
                if (!ledger.Apply(addr)) throw new InvalidOperationException("UDP FLOW metadata capacity exceeded");
            }
        }
        catch (Exception ex)
        {
            _healthy = false;
            _logger.LogWarning(ex, "UDP ownership evidence lost");
            Failed?.Invoke("Observação UDP perdida; captura parada por segurança.");
        }
    }

    public void Dispose() => Stop();
}

/// <summary>Pure event ledger: PID + endpoint identity + full five-tuple, never just ports.</summary>
internal sealed class UdpFlowLedger(uint processId)
{
    private readonly object _lock = new();
    private readonly HashSet<(ulong EndpointId, ProcessNetworkScanner.ProcessEndpoint Tuple)> _flows = new();
    public uint ProcessId { get; } = processId;

    public bool Apply(WinDivertNative.Address addr)
    {
        var f = addr.Flow;
        if (addr.LayerValue != (byte)WinDivertNative.Layer.Flow || f.ProcessId != ProcessId) return true;
        lock (_lock)
        {
            if ((addr.EventValue != (byte)WinDivertNative.Event.FlowEstablished &&
                 addr.EventValue != (byte)WinDivertNative.Event.FlowDeleted) || addr.IPv6 ||
                f.Protocol != 17 || f.EndpointId == 0 || f.LocalPort == 0 || f.RemotePort == 0 ||
                !IsMappedUnicast(f.LocalAddr0, f.LocalAddr1, f.LocalAddr2, f.LocalAddr3) ||
                !IsMappedUnicast(f.RemoteAddr0, f.RemoteAddr1, f.RemoteAddr2, f.RemoteAddr3)) return true;

            // WinDivert uses host-order UINT32[4], low word first, for mapped addresses.
            // The scanner/IPAddress(uint) uses IPv4 network bytes in a little-endian uint.
            var tuple = new ProcessNetworkScanner.ProcessEndpoint(17, f.LocalPort, f.RemotePort,
                BinaryPrimitives.ReverseEndianness(f.LocalAddr0), BinaryPrimitives.ReverseEndianness(f.RemoteAddr0));
            var key = (f.EndpointId, tuple);
            if (addr.EventValue == (byte)WinDivertNative.Event.FlowDeleted) { _flows.Remove(key); return true; }
            if (_flows.Count >= 256 && !_flows.Contains(key)) { _flows.Clear(); return false; }
            _flows.Add(key);
            return true;
        }
    }

    public ProcessNetworkScanner.ProcessEndpoint[] Snapshot()
    {
        lock (_lock) return _flows.Select(f => f.Tuple).Distinct().ToArray();
    }

    private static bool IsMappedUnicast(uint a0, uint a1, uint a2, uint a3) =>
        a1 == 0xffff && a2 == 0 && a3 == 0 && a0 != 0 && (a0 >> 24) != 0 && (a0 >> 24) < 224;
}
