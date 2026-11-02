using System.Diagnostics;

namespace DarkDesync.Core;

// PacketEngine owns serialization and the native handle. This scheduler never sends,
// changes packet bytes/address, reads wall time, or retains anything beyond a pulse.
internal sealed class OutboundPulseScheduler
{
    internal enum Decision { Held, Drop, Pass, Overflow }
    private sealed class Lane
    {
        internal readonly List<ThrottledPacket> Packets = new();
        internal long Epoch = -1, LastDue;
        internal double Correlation;
        internal bool Bad, GapOpen;
    }
    private readonly Dictionary<PulseShaper.Flow, Lane> _lanes = new();
    private readonly List<Lane> _order = new();
    private readonly DiagnosticMode _mode;
    private readonly long _start, _deadline;
    private readonly Random _random;
    private long _last, _slot = -1, _next;
    private int _cursor, _slotPackets, _slotBytes;
    private double _ema;
    internal int Count { get; private set; }
    internal long Bytes { get; private set; }
    internal long ReorderedPairs { get; private set; }
    internal long IntentionalDrops { get; private set; }
    internal double TargetBytesPerSecond { get; private set; }
    internal double LastSampleDelayMs { get; private set; }
    internal bool BadState { get; private set; }
    internal static bool Handles(DiagnosticMode mode) => mode is >= DiagnosticMode.TimeSlotBursting and <= DiagnosticMode.GilbertElliottBurstLoss;
    internal static long T(double ms) => (long)(ms * Stopwatch.Frequency / 1000);
    internal OutboundPulseScheduler(DiagnosticMode mode, long start, long deadline, int seed)
    {
        if (!Handles(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode; _start = _last = start; _deadline = deadline; _random = new Random(seed);
    }
    internal Decision Enqueue(ThrottledPacket packet, PulseShaper.Flow flow, long now)
    {
        now = _last = Math.Max(_last, now);
        if (now >= _deadline || !packet.Address.Outbound || flow.Protocol is not (6 or 17) ||
            (_mode == DiagnosticMode.GapBasedReordering && flow.Protocol != 17)) return Decision.Pass;
        if (Count >= 512 || Bytes + packet.Length > 786432 || packet.Length is 0 or > 1536) return Decision.Overflow;
        if (!_lanes.TryGetValue(flow, out var lane))
        {
            if (_lanes.Count >= 128) return Decision.Overflow;
            lane = new Lane(); _lanes.Add(flow, lane); _order.Add(lane);
        }
        if (_mode == DiagnosticMode.GilbertElliottBurstLoss)
        {
            // Transition per packet: P(G->B)=.08, P(B->G)=.18;
            // P(drop|G)=.01, P(drop|B)=.85. TCP loss is intentional too.
            if (_random.NextDouble() < (lane.Bad ? .18 : .08)) lane.Bad = !lane.Bad;
            BadState = lane.Bad;
            if (_random.NextDouble() < (lane.Bad ? .85 : .01))
            { IntentionalDrops++; return Decision.Drop; }
            return Decision.Pass;
        }
        long due = now;
        if (_mode == DiagnosticMode.CorrelatedOutboundJitter)
        {
            long epoch = (now - _start) / Math.Max(1, T(20));
            if (epoch != lane.Epoch)
            {
                // One AR(1) innovation per observed epoch, per full flow tuple.
                double decay = Math.Pow(.85, Math.Min(1024, epoch - lane.Epoch));
                lane.Correlation = decay * lane.Correlation + Math.Sqrt(1 - decay * decay) * (_random.NextDouble() * 2 - 1);
                lane.Epoch = epoch;
            }
            LastSampleDelayMs = Math.Clamp(45 + 35 * lane.Correlation, 5, 100);
            due = now + T(LastSampleDelayMs);
        }
        else if (_mode == DiagnosticMode.HeavyTailJitter)
        {
            // Pareto alpha=1.4, xm=4ms, hard TTL=140ms. Not uniform jitter.
            LastSampleDelayMs = Math.Min(140, 4 / Math.Pow(1 - _random.NextDouble(), 1 / 1.4));
            due = now + T(LastSampleDelayMs);
        }
        else if (_mode == DiagnosticMode.GapBasedReordering)
        {
            // At most one successor overtakes the held head. A lone head expires
            // after 24ms; it never waits indefinitely for a future packet.
            due = now + T(24);
        }
        // FIFO for both jitter distributions, including a decreasing next sample.
        if (_mode is DiagnosticMode.CorrelatedOutboundJitter or DiagnosticMode.HeavyTailJitter)
            due = Math.Max(due, lane.LastDue);
        lane.LastDue = packet.ReleaseAtTicks = Math.Min(due, _deadline);
        lane.Packets.Add(packet); Count++; Bytes += packet.Length;
        return Decision.Held;
    }
    internal bool TryDequeue(long now, out ThrottledPacket? packet)
    {
        packet = null; now = _last = Math.Max(_last, now);
        if (Count == 0) return false;
        // Deadline release is FIFO per lane, never intentional reordering at STOP.
        bool expired = now >= _deadline;
        if (!expired && _mode == DiagnosticMode.TimeSlotBursting)
        {
            long elapsed = now - _start, slot = elapsed / Math.Max(1, T(40));
            if (elapsed % Math.Max(1, T(40)) >= T(8)) return false;
            if (slot != _slot) { _slot = slot; _slotPackets = _slotBytes = 0; }
            if (_slotPackets >= 4) return false;
        }
        if (!expired && _mode is DiagnosticMode.SawtoothPacing or DiagnosticMode.TwoPhaseDrain or DiagnosticMode.AdaptiveQueueShaping)
        {
            if (now < _next) return false;
            if (_mode == DiagnosticMode.TwoPhaseDrain && now < _start + (_deadline - _start) / 3) return false;
        }
        for (int i = 0; i < _order.Count; i++)
        {
            var lane = _order[_cursor]; _cursor = (_cursor + 1) % _order.Count;
            if (lane.Packets.Count == 0) continue;
            int index = 0;
            if (!expired && _mode == DiagnosticMode.GapBasedReordering)
            {
                if (!lane.GapOpen && now < lane.Packets[0].ReleaseAtTicks)
                {
                    if (lane.Packets.Count < 2) continue;
                    index = 1; lane.GapOpen = true; ReorderedPairs++;
                }
                else lane.GapOpen = false;
            }
            else if (!expired && now < lane.Packets[0].ReleaseAtTicks) continue;
            var candidate = lane.Packets[index];
            if (!expired && _mode == DiagnosticMode.TimeSlotBursting && _slotBytes + candidate.Length > 3072) continue;
            packet = candidate; lane.Packets.RemoveAt(index); Count--; Bytes -= packet.Length;
            _slotPackets++; _slotBytes += (int)packet.Length;
            if (!expired)
            {
                double elapsedMs = (double)(now - _start) * 1000 / Stopwatch.Frequency;
                TargetBytesPerSecond = _mode switch
                {
                    DiagnosticMode.SawtoothPacing => 4096 + 28672 * (elapsedMs % 120) / 120,
                    DiagnosticMode.TwoPhaseDrain => 8192 + 24576 * Math.Clamp((double)(now - _start) / Math.Max(1, _deadline - _start), 0, 1),
                    DiagnosticMode.AdaptiveQueueShaping => 4096 + 2048 * (_ema = .75 * _ema + .25 * (Count + 1)),
                    _ => 0
                };
                TargetBytesPerSecond = Math.Min(65536, TargetBytesPerSecond);
                if (TargetBytesPerSecond > 0) _next = now + Math.Max(1, (long)(packet.Length * Stopwatch.Frequency / TargetBytesPerSecond));
            }
            return true;
        }
        return false;
    }
    internal IEnumerable<ThrottledPacket> Drain()
    {
        foreach (var lane in _order)
            foreach (var packet in lane.Packets) yield return packet;
        _lanes.Clear(); _order.Clear(); Count = 0; Bytes = 0; _cursor = 0;
    }
}
