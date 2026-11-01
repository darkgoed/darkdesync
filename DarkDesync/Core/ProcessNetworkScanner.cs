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
