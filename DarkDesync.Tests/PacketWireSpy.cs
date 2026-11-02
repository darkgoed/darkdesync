using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

internal sealed class PacketWireSpy : IDisposable
{
    internal sealed record Frame(byte[] Bytes, long Timestamp, long ObservedAt);
    private readonly IntPtr _handle;
    private readonly Task _reader;
    private int _stopped;
    internal ConcurrentQueue<Frame> Frames { get; } = new();
    internal PacketWireSpy(string filter, short priority)
    {
        _handle = WinDivertNative.WinDivertOpen(filter, WinDivertNative.Layer.Network, priority, WinDivertNative.FlagSniff | WinDivertNative.FlagRecvOnly);
        Assert.NotEqual(WinDivertNative.InvalidHandle, _handle);
        _reader = Task.Factory.StartNew(() =>
        {
            IntPtr p = Marshal.AllocHGlobal(65535);
            try
            {
                while (Volatile.Read(ref _stopped) == 0)
                {
                    var address = new WinDivertNative.Address();
                    if (!WinDivertNative.WinDivertRecv(_handle, p, 65535, out uint length, ref address)) break;
                    if (Frames.Count >= 256) continue;
                    byte[] bytes = new byte[length]; Marshal.Copy(p, bytes, 0, (int)length);
                    Frames.Enqueue(new(bytes, address.Timestamp, Stopwatch.GetTimestamp()));
                }
            }
            finally { Marshal.FreeHGlobal(p); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    internal static void InjectOwnLoopback(IntPtr injector, byte[] packet)
    {
        bool v6 = packet[0] >> 4 == 6;
        Assert.True(v6 ? packet.AsSpan(8,16).SequenceEqual(System.Net.IPAddress.IPv6Loopback.GetAddressBytes()) && packet.AsSpan(24,16).SequenceEqual(System.Net.IPAddress.IPv6Loopback.GetAddressBytes())
            : packet.AsSpan(12,4).SequenceEqual(System.Net.IPAddress.Loopback.GetAddressBytes()) && packet.AsSpan(16,4).SequenceEqual(System.Net.IPAddress.Loopback.GetAddressBytes()));
        IntPtr p = Marshal.AllocHGlobal(packet.Length);
        try
        {
            Marshal.Copy(packet,0,p,packet.Length);
            var address = new WinDivertNative.Address { Flags = (1u<<17) | (1u<<18) | (v6?1u<<20:1u<<21) | (1u<<22) | (1u<<23) };
            Assert.True(WinDivertNative.WinDivertSend(injector,p,(uint)packet.Length,out uint sent,ref address), "Own loopback test injection failed: "+Marshal.GetLastWin32Error());
            Assert.Equal(packet.Length,(int)sent);
        }
        finally { Marshal.FreeHGlobal(p); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped,1)!=0) return;
        WinDivertNative.WinDivertShutdown(_handle,WinDivertNative.Shutdown.Recv);
        _reader.GetAwaiter().GetResult(); WinDivertNative.WinDivertClose(_handle);
    }
}
