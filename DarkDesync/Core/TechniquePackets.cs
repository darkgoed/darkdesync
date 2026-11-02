using System.Buffers.Binary;
using System.Net;

namespace DarkDesync.Core;

// Bounded parser for IPv4 (including options) and IPv6 without extension headers.
// Fragments, malformed/offload length variants and unsupported protocols pass.
internal static class TechniquePackets
{
    internal readonly record struct Layout(bool IPv6, byte Protocol, int Transport, int TransportLength, int HeaderLength, bool PureAck);
    internal static bool TryParse(ReadOnlySpan<byte> p, out Layout layout)
    {
        layout = default;
        if (p.Length < 20 || p.Length > 65535) return false;
        bool v6 = p[0] >> 4 == 6; int offset; byte protocol;
        if (v6)
        {
            if (p.Length < 40 || BinaryPrimitives.ReadUInt16BigEndian(p[4..]) + 40 != p.Length) return false;
            offset = 40; protocol = p[6];
        }
        else
        {
            if (p[0] >> 4 != 4 || BinaryPrimitives.ReadUInt16BigEndian(p[2..]) != p.Length ||
                (BinaryPrimitives.ReadUInt16BigEndian(p[6..]) & 0xbfff) != 0) return false;
            offset = (p[0] & 15) * 4; protocol = p[9];
            if (offset < 20 || offset > p.Length) return false;
        }
        int length = p.Length - offset, header;
        if (protocol == 17)
        {
            if (length < 8 || BinaryPrimitives.ReadUInt16BigEndian(p[(offset + 4)..]) != length) return false;
            header = 8;
        }
        else if (protocol == 6)
        {
            if (length < 20) return false;
            header = (p[offset + 12] >> 4) * 4;
            if (header < 20 || header > length) return false;
        }
        else return false;
        bool pure = protocol == 6 && length == header && p[offset + 13] == 0x10 && (p[offset + 12] & 15) == 0;
        layout = new(v6, protocol, offset, length, header, pure); return true;
    }
    internal static PulseShaper.Flow Flow(ReadOnlySpan<byte> p, Layout l) => new(l.Protocol,
        new IPAddress(p.Slice(l.IPv6 ? 8 : 12, l.IPv6 ? 16 : 4)).ToString(), BinaryPrimitives.ReadUInt16BigEndian(p[l.Transport..]),
        new IPAddress(p.Slice(l.IPv6 ? 24 : 16, l.IPv6 ? 16 : 4)).ToString(), BinaryPrimitives.ReadUInt16BigEndian(p[(l.Transport + 2)..]));
    internal static byte TrafficClass(ReadOnlySpan<byte> p, Layout l) => l.IPv6 ? (byte)(((p[0] & 15) << 4) | (p[1] >> 4)) : p[1];
    internal static bool TryMark(ReadOnlySpan<byte> p, DiagnosticMode mode, int dscp, out byte[]? modified)
    {
        modified = null;
        if (p.Length > 4096 || !TryParse(p, out var l)) return false;
        byte before = TrafficClass(p, l);
        byte after = mode switch
        {
            DiagnosticMode.EcnCongestionMarking when (before & 3) is 1 or 2 => (byte)(before | 3),
            DiagnosticMode.DscpTrafficMarking when dscp is >= 0 and <= 63 => (byte)((dscp << 2) | (before & 3)),
            _ => before
        };
        if (before == after) return false;
        modified = p.ToArray();
        if (l.IPv6) { modified[0] = (byte)((modified[0] & 0xf0) | (after >> 4)); modified[1] = (byte)((modified[1] & 15) | (after << 4)); }
        else modified[1] = after;
        RepairChecksums(modified, l); return true;
    }
    internal static uint Sum(ReadOnlySpan<byte> bytes, uint sum = 0)
    {
        int i = 0; for (; i + 1 < bytes.Length; i += 2) sum += (uint)((bytes[i] << 8) | bytes[i + 1]);
        if (i < bytes.Length) sum += (uint)bytes[i] << 8;
        return sum;
    }
    internal static ushort Finish(uint sum)
    {
        while ((sum >> 16) != 0) sum = (sum & 65535) + (sum >> 16);
        return (ushort)~sum;
    }
    internal static void RepairChecksums(Span<byte> p, Layout l)
    {
        if (!l.IPv6)
        {
            p[10] = p[11] = 0;
            BinaryPrimitives.WriteUInt16BigEndian(p[10..], Finish(Sum(p[..l.Transport])));
        }
        int checksumOffset = l.Transport + (l.Protocol == 17 ? 6 : 16);
        p[checksumOffset] = p[checksumOffset + 1] = 0;
        uint sum = Sum(p.Slice(l.IPv6 ? 8 : 12, l.IPv6 ? 32 : 8));
        sum += (uint)l.TransportLength + l.Protocol; // upper length word is zero, <=65535
        ushort checksum = Finish(Sum(p[l.Transport..], sum));
        if (l.Protocol == 17 && checksum == 0) checksum = 65535;
        BinaryPrimitives.WriteUInt16BigEndian(p[checksumOffset..], checksum);
    }
}
