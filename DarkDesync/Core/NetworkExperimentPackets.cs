using System.Buffers.Binary;
using System.Net;

namespace DarkDesync.Core;

// Independent laboratory codecs. Never called by the global pulse engine.
internal static class NetworkExperimentPackets
{
    internal sealed record TcpOptions(int? WindowScale, bool SackPermitted, IReadOnlyList<string> SackBlocks,
        uint? TSval, uint? TSecr);
    internal sealed record TcpObservation(string Flow, bool Syn, bool Ack, ushort Window, TcpOptions Options);

    internal static bool TryTcp(ReadOnlySpan<byte> p, out TcpObservation? observation)
    {
        observation = null;
        if (!TechniquePackets.TryParse(p, out var l) || l.Protocol != 6) return false;
        var tcp = p[l.Transport..];
        int? scale = null; bool sack = false; uint? ts = null, echo = null;
        var blocks = new List<string>();
        bool syn = (tcp[13] & 2) != 0;
        for (int i = 20; i < l.HeaderLength;)
        {
            byte kind = tcp[i];
            if (kind == 0) break;
            if (kind == 1) { i++; continue; }
            if (i + 1 >= l.HeaderLength) return false;
            int len = tcp[i + 1];
            if (len < 2 || i + len > l.HeaderLength) return false;
            switch (kind)
            {
                case 3:
                    if (len != 3 || !syn || tcp[i + 2] > 14) return false;
                    scale = tcp[i + 2]; break;
                case 4:
                    if (len != 2 || !syn) return false;
                    sack = true; break;
                case 5:
                    if (syn || len < 10 || (len - 2) % 8 != 0) return false;
                    for (int j = i + 2; j < i + len; j += 8)
                        blocks.Add($"{BinaryPrimitives.ReadUInt32BigEndian(tcp[j..])}:{BinaryPrimitives.ReadUInt32BigEndian(tcp[(j + 4)..])}");
                    break;
                case 8:
                    if (len != 10) return false;
                    ts = BinaryPrimitives.ReadUInt32BigEndian(tcp[(i + 2)..]);
                    echo = BinaryPrimitives.ReadUInt32BigEndian(tcp[(i + 6)..]); break;
            }
            i += len;
        }
        var flow = TechniquePackets.Flow(p, l);
        observation = new($"{flow.Source}:{flow.SourcePort}>{flow.Destination}:{flow.DestinationPort}",
            syn, (tcp[13] & 16) != 0, BinaryPrimitives.ReadUInt16BigEndian(tcp[14..]), new(scale, sack, blocks, ts, echo));
        return true;
    }

    internal static uint FlowLabel(ReadOnlySpan<byte> p)
    {
        if (p.Length < 40 || p[0] >> 4 != 6) throw new ArgumentException("IPv6 header required");
        return BinaryPrimitives.ReadUInt32BigEndian(p) & 0xfffff;
    }

    internal static byte[] OwnUdp6(int source, int destination, uint label, ReadOnlySpan<byte> payload)
    {
        if (source is < 1 or > 65535 || destination is < 1 or > 65535 || label > 0xfffff || payload.Length > 1200)
            throw new ArgumentOutOfRangeException(nameof(label));
        var p = new byte[48 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(p, 0x62a00000u | label); // IPv6, traffic class 0x2a
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)(p.Length - 40));
        p[6] = 17; p[7] = 64;
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(p, 8);
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(p, 24);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(40), (ushort)source);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(42), (ushort)destination);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(44), (ushort)(payload.Length + 8));
        payload.CopyTo(p.AsSpan(48));
        if (!TechniquePackets.TryParse(p, out var layout)) throw new InvalidOperationException("Invalid own packet");
        TechniquePackets.RepairChecksums(p, layout);
        return p;
    }
}
