using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DarkDesync.Core;

/// <summary>
/// Scans active TCP/UDP connections for a specific process using 
/// GetExtendedTcpTable / GetExtendedUdpTable Win32 APIs.
/// This is used to discover which ports belong to the target process
/// so we can build precise WinDivert filters.
/// </summary>
public sealed class ProcessNetworkScanner
{
    private readonly ILogger<ProcessNetworkScanner> _logger;

    public ProcessNetworkScanner(ILogger<ProcessNetworkScanner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Represents a network endpoint owned by a process.
    /// </summary>
    public record ProcessEndpoint(
        byte Protocol,      // 6 = TCP, 17 = UDP
        ushort LocalPort,
        ushort RemotePort,
        uint LocalAddr,
        uint RemoteAddr
    );

    internal record UdpBinding(uint ProcessId, ushort Port, uint LocalAddr, bool IPv6 = false);

    // Negative ambiguity check ONLY. The positive identity proof is a live PID FLOW
    // event with a complete tuple, held by FlowTracker, not this socket table.
    internal bool IsExclusiveUdpBinding(uint pid, ProcessEndpoint flow)
    {
        try
        {
            var bindings = ReadUdpBindings(AF_INET);
            bindings.AddRange(ReadUdpBindings(23)); // AF_INET6: also reject dual-stack collisions.
            return HasExclusiveUdpBinding(pid, flow, bindings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot exclude UDP binding ambiguity");
            return false;
        }
    }

    internal static bool HasExclusiveUdpBinding(uint pid, ProcessEndpoint flow, IReadOnlyList<UdpBinding> bindings)
    {
        if (pid == 0 || flow.Protocol != 17 || flow.LocalPort == 0 || flow.RemotePort == 0 ||
            flow.LocalAddr == 0 || flow.RemoteAddr == 0) return false;
        var matching = bindings.Where(b => b.Port == flow.LocalPort).ToArray();
        // Conservatively reject reuse even on different local IPs or by the same PID.
        return matching.Length == 1 && !matching[0].IPv6 && matching[0].ProcessId == pid &&
            (matching[0].LocalAddr == 0 || matching[0].LocalAddr == flow.LocalAddr);
    }

    private static List<UdpBinding> ReadUdpBindings(int family)
    {
        int size = 0;
        uint result = GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if ((result != 0 && result != 122) || size < 4 || size > 8 * 1024 * 1024)
                throw new System.ComponentModel.Win32Exception((int)result, "UDP owner table unavailable");
            int allocated = size;
            IntPtr buffer = Marshal.AllocHGlobal(allocated);
            try
            {
                result = GetExtendedUdpTable(buffer, ref size, false, family, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
                if (result == 122) continue;
                if (result != 0) throw new System.ComponentModel.Win32Exception((int)result);
                int count = Marshal.ReadInt32(buffer);
                int rowSize = family == AF_INET ? 12 : 28;
                if (count < 0 || count > (allocated - 4) / rowSize) throw new InvalidOperationException("Invalid UDP owner table");
                var bindings = new List<UdpBinding>(count);
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = buffer + 4 + i * rowSize;
                    int portOffset = family == AF_INET ? 4 : 20;
                    ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, portOffset));
                    uint owner = unchecked((uint)Marshal.ReadInt32(row, portOffset + 4));
                    uint ip = family == AF_INET ? unchecked((uint)Marshal.ReadInt32(row)) : 0;
                    bindings.Add(new(owner, port, ip, family != AF_INET));
                }
                return bindings;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("UDP owner table kept changing; no reliable snapshot");
    }

    /// <summary>
    /// Gets all TCP and UDP endpoints for the specified process.
    /// </summary>
    public List<ProcessEndpoint> GetEndpointsForProcess(uint processId)
    {
        var endpoints = new List<ProcessEndpoint>();

        try
        {
            GetTcpEndpoints(processId, endpoints);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate TCP connections for PID {Pid}", processId);
        }

        try
        {
            GetUdpEndpoints(processId, endpoints);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate UDP connections for PID {Pid}", processId);
        }

        return endpoints;
    }

    /// <summary>
    /// Gets just the unique local ports used by the process.
    /// </summary>
    public HashSet<ushort> GetLocalPortsForProcess(uint processId)
    {
        var endpoints = GetEndpointsForProcess(processId);
        var ports = new HashSet<ushort>();
        foreach (var ep in endpoints)
        {
            if (ep.LocalPort != 0)
                ports.Add(ep.LocalPort);
        }
        return ports;
    }

    private void GetTcpEndpoints(uint processId, List<ProcessEndpoint> endpoints)
    {
        int size = 0;
        // First call to get required buffer size
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false,
            AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, false,
                AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);

            if (result != 0)
            {
                _logger.LogWarning("GetExtendedTcpTable failed with error {Error}", result);
                return;
            }

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = buffer + 4;
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                if (row.dwOwningPid == processId && row.dwState == 5)
                {
                    ushort localPort = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)row.dwLocalPort);
                    ushort remotePort = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)row.dwRemotePort);

                    endpoints.Add(new ProcessEndpoint(
                        Protocol: 6, // TCP
                        LocalPort: localPort,
                        RemotePort: remotePort,
                        LocalAddr: row.dwLocalAddr,
                        RemoteAddr: row.dwRemoteAddr
                    ));
                }
                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void GetUdpEndpoints(uint processId, List<ProcessEndpoint> endpoints)
    {
        int size = 0;
        uint result = GetExtendedUdpTable(IntPtr.Zero, ref size, false,
            AF_INET, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedUdpTable(buffer, ref size, false,
                AF_INET, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);

            if (result != 0)
            {
                _logger.LogWarning("GetExtendedUdpTable failed with error {Error}", result);
                return;
            }

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = buffer + 4;
            int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(rowPtr);
                if (row.dwOwningPid == processId)
                {
                    ushort localPort = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)row.dwLocalPort);

                    endpoints.Add(new ProcessEndpoint(
                        Protocol: 17, // UDP
                        LocalPort: localPort,
                        RemotePort: 0,
                        LocalAddr: row.dwLocalAddr,
                        RemoteAddr: 0
                    ));
                }
                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // --- Win32 API declarations ---

    private const int AF_INET = 2;

    private enum TCP_TABLE_CLASS
    {
        TCP_TABLE_BASIC_LISTENER,
        TCP_TABLE_BASIC_CONNECTIONS,
        TCP_TABLE_BASIC_ALL,
        TCP_TABLE_OWNER_PID_LISTENER,
        TCP_TABLE_OWNER_PID_CONNECTIONS,
        TCP_TABLE_OWNER_PID_ALL,
        TCP_TABLE_OWNER_MODULE_LISTENER,
        TCP_TABLE_OWNER_MODULE_CONNECTIONS,
        TCP_TABLE_OWNER_MODULE_ALL
    }

    private enum UDP_TABLE_CLASS
    {
        UDP_TABLE_BASIC,
        UDP_TABLE_OWNER_PID,
        UDP_TABLE_OWNER_MODULE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public int dwLocalPort;
        public uint dwRemoteAddr;
        public int dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public int dwLocalPort;
        public uint dwOwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, TCP_TABLE_CLASS tblClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, UDP_TABLE_CLASS tblClass, uint reserved);
}
