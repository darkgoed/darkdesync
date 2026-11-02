using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DarkDesync.Core;

// Sniff-only: no packet is ever withheld. Filters are generated only from owned sockets.
internal sealed class ExperimentCapture : IDisposable
{
    private readonly IntPtr _handle;
    private readonly Task _reader;
    private int _stopped;
    internal ConcurrentQueue<byte[]> Packets { get; } = new();
    internal int ReadError { get; private set; }

    internal ExperimentCapture(string filter)
    {
        _handle = WinDivertNative.WinDivertOpen(filter, WinDivertNative.Layer.Network, -1100,
            WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly);
        if (_handle == WinDivertNative.InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
        _reader = Task.Factory.StartNew(() =>
        {
            IntPtr p = Marshal.AllocHGlobal(65535);
            try
            {
                while (Volatile.Read(ref _stopped) == 0)
                {
                    var address = new WinDivertNative.Address();
                    if (!WinDivertNative.WinDivertRecv(_handle, p, 65535, out uint length, ref address))
                    { if (Volatile.Read(ref _stopped) == 0) ReadError = Marshal.GetLastWin32Error(); break; }
                    if (Packets.Count >= 128) continue;
                    var bytes = new byte[length]; Marshal.Copy(p, bytes, 0, (int)length); Packets.Enqueue(bytes);
                }
            }
            finally { Marshal.FreeHGlobal(p); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal static void SendOwnUdp6(byte[] packet)
    {
        if (!TechniquePackets.TryParse(packet, out var l) || !l.IPv6 || l.Protocol != 17 ||
            !packet.AsSpan(8, 16).SequenceEqual(System.Net.IPAddress.IPv6Loopback.GetAddressBytes()) ||
            !packet.AsSpan(24, 16).SequenceEqual(System.Net.IPAddress.IPv6Loopback.GetAddressBytes()))
            throw new ArgumentException("Only own IPv6 UDP loopback packets");
        IntPtr handle = WinDivertNative.WinDivertOpen("false", WinDivertNative.Layer.Network, 1100, WinDivertNative.FlagSendOnly);
        if (handle == WinDivertNative.InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr p = Marshal.AllocHGlobal(packet.Length);
        try
        {
            Marshal.Copy(packet, 0, p, packet.Length);
            // Fresh generated packet, never a captured impostor reinjection; hop limit fixed at 64.
            var address = new WinDivertNative.Address { Flags = (1u << 17) | (1u << 18) | (1u << 20) | (1u << 23) };
            if (!WinDivertNative.WinDivertSend(handle, p, (uint)packet.Length, out uint sent, ref address) || sent != packet.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(p); WinDivertNative.WinDivertClose(handle); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        WinDivertNative.WinDivertShutdown(_handle, WinDivertNative.Shutdown.Recv);
        _reader.GetAwaiter().GetResult();
        WinDivertNative.WinDivertClose(_handle);
    }
}
