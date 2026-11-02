using System.Diagnostics;

namespace DarkDesync.Core;

internal sealed class AckPulseQueue(long deadline)
{
    private sealed class Lane { internal readonly Queue<ThrottledPacket> Packets = new(); internal long LastDue; }
    private readonly Dictionary<PulseShaper.Flow, Lane> _lanes = new();
    private readonly List<Lane> _order = new();
    private int _cursor;
    private long _last;
    internal int Count { get; private set; }
    internal long Bytes { get; private set; }
    internal static long T(int ms) => (long)ms * Stopwatch.Frequency / 1000;
    internal bool Enqueue(PulseShaper.Flow flow, ThrottledPacket packet, long now)
    {
        now = _last = Math.Max(now, _last);
        if (Count >= 512 || Bytes + packet.Length > 786432 || now >= deadline) return false;
        if (!_lanes.TryGetValue(flow, out var lane))
        {
            if (_lanes.Count >= 128) return false;
            _lanes[flow] = lane = new Lane(); _order.Add(lane);
        }
        packet.ReleaseAtTicks = Math.Min(deadline, Math.Min(now + T(64), Math.Max(now + T(8), lane.LastDue + T(12))));
        lane.LastDue = packet.ReleaseAtTicks; lane.Packets.Enqueue(packet); Count++; Bytes += packet.Length; return true;
    }
    internal bool TryDequeue(long now, out ThrottledPacket? packet)
    {
        packet = null; now = _last = Math.Max(now, _last);
        for (int i = 0; i < _order.Count; i++)
        {
            var lane = _order[_cursor]; _cursor = (_cursor + 1) % _order.Count;
            if (!lane.Packets.TryPeek(out var head) || head.ReleaseAtTicks > now) continue;
            packet = lane.Packets.Dequeue(); Count--; Bytes -= packet.Length; return true;
        }
        return false;
    }
    internal IEnumerable<ThrottledPacket> DrainFlow(PulseShaper.Flow flow)
    {
        if (!_lanes.TryGetValue(flow, out var lane)) yield break;
        while (lane.Packets.TryDequeue(out var p)) { Count--; Bytes -= p.Length; yield return p; }
        lane.LastDue = 0;
    }
    internal IEnumerable<ThrottledPacket> Drain()
    {
        foreach (var lane in _order)
            while (lane.Packets.TryDequeue(out var p)) { Count--; Bytes -= p.Length; yield return p; }
        _lanes.Clear(); _order.Clear(); _cursor = 0;
    }
}
