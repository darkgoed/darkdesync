using System.Diagnostics;

namespace DarkDesync.Core;

// Packet lock owned by caller. No per-packet task, timer or busy wait.
internal sealed class TemporalPulseQueue(DiagnosticMode mode, long pulseDeadline)
{
    private sealed class Lane
    {
        internal readonly Queue<(ThrottledPacket Packet, long Arrival)> Packets = new();
        internal long LastArrival, LastRelease, LastDeadline;
        internal bool HasArrival, HasRelease;
        internal double GapMs = 8;
    }
    private readonly Dictionary<PulseShaper.Flow, Lane> _lanes = new();
    private readonly List<Lane> _order = new();
    private int _cursor;
    private long _last;
    internal int Count { get; private set; }
    internal long Bytes { get; private set; }
    internal static long T(double ms) => (long)(ms * Stopwatch.Frequency / 1000);
    internal bool Enqueue(PulseShaper.Flow flow, ThrottledPacket packet, long now)
    {
        now = _last = Math.Max(now, _last);
        if (Count >= 512 || Bytes + packet.Length > 786432 || now >= pulseDeadline) return false;
        if (!_lanes.TryGetValue(flow, out var lane))
        {
            if (_lanes.Count >= 128) return false;
            _lanes[flow] = lane = new Lane(); _order.Add(lane);
        }

        if (mode == DiagnosticMode.InterPacketGapEqualization)
        {
            if (lane.HasArrival)
            {
                double sample = (now - lane.LastArrival) * 1000d / Stopwatch.Frequency;
                lane.GapMs = Math.Clamp(lane.GapMs * .875 + sample * .125, 6, 24);
            }
            packet.ReleaseAtTicks = Math.Min(pulseDeadline, now + T(96)); // maximum age, not normal send time
        }
        else
        {
            // Small datagrams/ACKs get shorter latency budget, but never jump
            // ahead of earlier same-flow data. EDF considers FIFO heads only.
            packet.ReleaseAtTicks = Math.Min(pulseDeadline, Math.Max(lane.LastDeadline, now + T(packet.Length <= 256 ? 16 : 48)));
            lane.LastDeadline = packet.ReleaseAtTicks;
        }
        lane.LastArrival = now; lane.HasArrival = true; lane.Packets.Enqueue((packet, now)); Count++; Bytes += packet.Length; return true;
    }
    internal bool TryDequeue(long now, out ThrottledPacket? packet)
    {
        packet = null; now = _last = Math.Max(now, _last);
        int chosen = -1; long earliest = long.MaxValue;
        for (int i = 0; i < _order.Count; i++)
        {
            int index = (_cursor + i) % _order.Count; var lane = _order[index];
            if (!lane.Packets.TryPeek(out var item)) continue;
            var head = item.Packet;
            long due = mode == DiagnosticMode.InterPacketGapEqualization
                ? Math.Min(head.ReleaseAtTicks, Math.Max(item.Arrival + T(2), !lane.HasRelease ? item.Arrival + T(lane.GapMs) : lane.LastRelease + T(lane.GapMs)))
                : Math.Max(item.Arrival + T(2), head.ReleaseAtTicks - T(Math.Min(24, (Count - 1) * 2)));
            due = Math.Min(head.ReleaseAtTicks, due);
            if (now < due) continue;
            if (mode == DiagnosticMode.InterPacketGapEqualization) { chosen = index; break; }
            if (head.ReleaseAtTicks < earliest) { earliest = head.ReleaseAtTicks; chosen = index; }
        }
        if (chosen < 0) return false;
        var selected = _order[chosen]; packet = selected.Packets.Dequeue().Packet; selected.LastRelease = now;
        selected.HasRelease = true;
        Count--; Bytes -= packet.Length; _cursor = (chosen + 1) % _order.Count; return true;
    }
    internal IEnumerable<ThrottledPacket> Drain()
    {
        foreach (var lane in _order)
            while (lane.Packets.TryDequeue(out var item)) { Count--; Bytes -= item.Packet.Length; yield return item.Packet; }
        _lanes.Clear(); _order.Clear(); _cursor = 0;
    }
}
