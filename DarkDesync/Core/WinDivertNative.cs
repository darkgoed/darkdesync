using System.Runtime.InteropServices;

namespace DarkDesync.Core;

/// <summary>
/// P/Invoke declarations for WinDivert 2.2.2 native API.
/// Maps the C structures and functions from windivert.h to managed C# types.
/// </summary>
public static class WinDivertNative
{
    private const string DllName = "WinDivert.dll";

    // --- Enums ---

    public enum Layer : int
    {
        Network = 0,
        NetworkForward = 1,
        Flow = 2,
        Socket = 3,
        Reflect = 4,
    }

    public enum Event : int
    {
        NetworkPacket = 0,
        FlowEstablished = 1,
        FlowDeleted = 2,
        SocketBind = 3,
        SocketConnect = 4,
        SocketListen = 5,
        SocketAccept = 6,
        SocketClose = 7,
        ReflectOpen = 8,
        ReflectClose = 9,
    }

    public enum Param : int
    {
        QueueLength = 0,
        QueueTime = 1,
        QueueSize = 2,
        VersionMajor = 3,
        VersionMinor = 4,
    }

    public enum Shutdown : int
    {
        Recv = 0x1,
        Send = 0x2,
        Both = 0x3,
    }

    // --- Flags ---

    public const ulong FlagSniff = 0x0001;
    public const ulong FlagDrop = 0x0002;
    public const ulong FlagRecvOnly = 0x0004;
    public const ulong FlagSendOnly = 0x0008;
    public const ulong FlagNoInstall = 0x0010;
    public const ulong FlagFragments = 0x0020;

    // --- Constants ---

    public const int MtuMax = 40 + 0xFFFF;
    public const int BatchMax = 0xFF;

    // --- Structures ---

    /// <summary>
    /// WINDIVERT_DATA_NETWORK - Network layer address data.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DataNetwork
    {
        public uint IfIdx;
        public uint SubIfIdx;
    }

    /// <summary>
    /// WINDIVERT_DATA_FLOW - Flow layer address data.
    /// Contains ProcessId for process-level tracking.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DataFlow
    {
        public ulong EndpointId;
        public ulong ParentEndpointId;
        public uint ProcessId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] LocalAddr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] RemoteAddr;
        public ushort LocalPort;
        public ushort RemotePort;
        public byte Protocol;
    }

    /// <summary>
    /// WINDIVERT_DATA_SOCKET - Socket layer address data.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DataSocket
    {
        public ulong EndpointId;
        public ulong ParentEndpointId;
        public uint ProcessId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] LocalAddr;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] RemoteAddr;
        public ushort LocalPort;
        public ushort RemotePort;
        public byte Protocol;
    }

    /// <summary>
    /// WINDIVERT_ADDRESS - Main address structure returned by WinDivertRecv.
    /// Uses explicit layout to handle the C union of Network/Flow/Socket/Reflect data.
    /// Total size: 80 bytes (8 timestamp + 4 flags + 4 reserved + 64 union data).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct Address
    {
        [FieldOffset(0)]
        public long Timestamp;

        // Bitfields packed into a single uint32 at offset 8:
        // Bits 0-7:   Layer
        // Bits 8-15:  Event
        // Bit  16:    Sniffed
        // Bit  17:    Outbound
        // Bit  18:    Loopback
        // Bit  19:    Impostor
        // Bit  20:    IPv6
        // Bit  21:    IPChecksum
        // Bit  22:    TCPChecksum
        // Bit  23:    UDPChecksum
        // Bits 24-31: Reserved1
        [FieldOffset(8)]
        public uint Flags;

        [FieldOffset(12)]
        public uint Reserved2;

        // Union data starts at offset 16, 64 bytes
        [FieldOffset(16)]
        public DataNetwork Network;

        [FieldOffset(16)]
        public DataFlowRaw Flow;

        [FieldOffset(16)]
        public DataSocketRaw Socket;

        public byte LayerValue => (byte)(Flags & 0xFF);
        public byte EventValue => (byte)((Flags >> 8) & 0xFF);
        public bool Sniffed => ((Flags >> 16) & 1) != 0;
        public bool Outbound => ((Flags >> 17) & 1) != 0;
        public bool Loopback => ((Flags >> 18) & 1) != 0;
        public bool IPv6 => ((Flags >> 20) & 1) != 0;
    }

    /// <summary>
    /// Raw flow data without managed array marshaling (for use in explicit-layout struct).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DataFlowRaw
    {
        public ulong EndpointId;
        public ulong ParentEndpointId;
        public uint ProcessId;
        public uint LocalAddr0, LocalAddr1, LocalAddr2, LocalAddr3;
        public uint RemoteAddr0, RemoteAddr1, RemoteAddr2, RemoteAddr3;
        public ushort LocalPort;
        public ushort RemotePort;
        public byte Protocol;
    }

    /// <summary>
    /// Raw socket data without managed array marshaling.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DataSocketRaw
    {
        public ulong EndpointId;
        public ulong ParentEndpointId;
        public uint ProcessId;
        public uint LocalAddr0, LocalAddr1, LocalAddr2, LocalAddr3;
        public uint RemoteAddr0, RemoteAddr1, RemoteAddr2, RemoteAddr3;
        public ushort LocalPort;
        public ushort RemotePort;
        public byte Protocol;
    }

    // --- P/Invoke Functions ---

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern IntPtr WinDivertOpen(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        Layer layer,
        short priority,
        ulong flags);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertRecv(
        IntPtr handle,
        IntPtr pPacket,
        uint packetLen,
        out uint recvLen,
        ref Address addr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertSend(
        IntPtr handle,
        IntPtr pPacket,
        uint packetLen,
        out uint sendLen,
        ref Address addr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertShutdown(
        IntPtr handle,
        Shutdown how);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertSetParam(
        IntPtr handle,
        Param param,
        ulong value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertGetParam(
        IntPtr handle,
        Param param,
        out ulong value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    public static extern bool WinDivertHelperCompileFilter(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        Layer layer,
        IntPtr obj,
        uint objLen,
        out IntPtr errorStr,
        out uint errorPos);

    // --- Invalid handle constant ---
    public static readonly IntPtr InvalidHandle = new IntPtr(-1);

    /// <summary>
    /// Validates a filter string without opening a handle.
    /// </summary>
    public static bool ValidateFilter(string filter, Layer layer, out string? error)
    {
        bool result = WinDivertHelperCompileFilter(filter, layer, IntPtr.Zero, 0, out IntPtr errorStr, out uint errorPos);
        if (!result && errorStr != IntPtr.Zero)
        {
            error = $"Position {errorPos}: {Marshal.PtrToStringAnsi(errorStr)}";
            return false;
        }
        error = null;
        return result;
    }
}
