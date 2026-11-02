using System.Diagnostics;

namespace DarkDesync.Core;

// Used only under PacketEngine's packet lock. Budgets are charged at actual release,
// not at enqueue: a delayed worker cannot replay an accumulated virtual schedule.
internal sealed class PulseShaper
{
    internal readonly record struct Flow(byte Protocol, string Source, ushort SourcePort, string Destination, ushort DestinationPort);
    private sealed class Lane(long now)
    {
        internal readonly Queue<ThrottledPacket> Packets = new();
        internal double Tokens;
        internal long Updated = now;
    }

    private readonly Dictionary<Flow, Lane> _lanes = new();
    private readonly List<Flow> _order = new();
    private readonly DiagnosticMode _mode;
    private readonly long _start, _holdEnd, _step;
    private long _updated;
    private double _tokens, _peak;
    private int _cursor;
    internal int Count { get; private set; }
    internal int FlowCount => _lanes.Count;

    internal PulseShaper(DiagnosticMode mode, long start, long deadline)
    {
        _mode = mode;
        _start = _updated = start;
        _holdEnd = start + (deadline - start) * 2 / 5;
        _step = start + (deadline - start) / 2;
        _tokens = mode == DiagnosticMode.DualTokenBucket ? 1024 : 0;
    }

    internal static bool Handles(DiagnosticMode mode) => mode is DiagnosticMode.HoldAndDrainShaping or
        DiagnosticMode.RateStepShaping or DiagnosticMode.DualTokenBucket or DiagnosticMode.FlowAwareShaping;

    internal bool Enqueue(ThrottledPacket packet, Flow flow)
    {
        // Oversized datagrams cannot fit the bounded burst: caller cancels and drains.
        // 512 * 1536 bytes plus at most 128 lanes; no unbounded flow history.
        if (Count >= 512 || packet.Length > 1536 || packet.Length == 0) return false;
        if (_mode != DiagnosticMode.FlowAwareShaping) flow = default;
        if (!_lanes.TryGetValue(flow, out var lane))
        {
            if (_lanes.Count >= 128) return false;
            lane = new Lane(_start);
            _lanes.Add(flow, lane);
            _order.Add(flow);
        }
        lane.Packets.Enqueue(packet);
        Count++;
        return true;
    }

    private static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;
    private void Refill(long now)
    {
        now = Math.Max(now, _updated); // Monotonic budget even with a regressing injected clock.
        double earned = _mode switch
        {
            DiagnosticMode.HoldAndDrainShaping => Seconds(Math.Max(0, now - Math.Max(_updated, _holdEnd))) * 32768,
            DiagnosticMode.RateStepShaping => Seconds(Math.Max(0, Math.Min(now, _step) - Math.Min(_updated, _step))) * 4096 +
                Seconds(Math.Max(0, now - Math.Max(_updated, _step))) * 32768,
            DiagnosticMode.DualTokenBucket => Seconds(now - _updated) * 8192,
            _ => 0
        };
        _tokens = Math.Min(_mode == DiagnosticMode.DualTokenBucket ? 2048 : 1536, _tokens + earned);
        _peak = Math.Min(1536, _peak + Seconds(now - _updated) * 32768);
        _updated = now;
    }

    internal bool TryDequeue(long now, out ThrottledPacket? packet)
    {
        packet = null;
        if (Count == 0) return false;
        Refill(now);
        for (int checkedLanes = 0; checkedLanes < _order.Count; checkedLanes++)
        {
            int index = _cursor;
            _cursor = (_cursor + 1) % _order.Count;
            var lane = _lanes[_order[index]];
            if (lane.Packets.Count == 0) continue;
            uint length = lane.Packets.Peek().Length; // Full IP bytes, never payload or packet count.
            if (_mode == DiagnosticMode.FlowAwareShaping)
            {
                long monotonic = Math.Max(now, lane.Updated);
                lane.Tokens = Math.Min(1536, lane.Tokens + Seconds(monotonic - lane.Updated) * 8192);
                lane.Updated = monotonic;
                if (lane.Tokens < length) continue;
                lane.Tokens -= length;
            }
            else
            {
                if (_mode == DiagnosticMode.HoldAndDrainShaping && now < _holdEnd) return false;
                if (_tokens < length || (_mode == DiagnosticMode.DualTokenBucket && _peak < length)) return false;
                _tokens -= length;
                if (_mode == DiagnosticMode.DualTokenBucket) _peak -= length;
            }
            packet = lane.Packets.Dequeue();
            Count--;
            return true;
        }
        return false;
    }

    internal IEnumerable<ThrottledPacket> Drain()
    {
        // No global ordering promise across flows. Every individual lane remains FIFO.
        foreach (var key in _order)
            while (_lanes[key].Packets.TryDequeue(out var packet)) { Count--; yield return packet; }
        _lanes.Clear();
        _order.Clear();
        _cursor = 0;
    }
}
