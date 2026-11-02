using System.Diagnostics;

namespace DarkDesync.Core;

// PacketEngine owns the lock, clock, lifetime and reinjection. No timers or native calls here.
internal sealed class ScopedPulseScheduler(DiagnosticMode mode, long start, int seed = 731)
{
    private sealed class Lane
    {
        internal readonly List<ThrottledPacket> Packets = [];
        internal long Next;
        internal bool CompletePair;
    }
    private readonly Dictionary<PulseShaper.Flow, Lane> _lanes = [];
    private readonly List<PulseShaper.Flow> _order = [];
    private readonly Random _random = new(seed);
    private readonly long _start = start;
    private readonly double[] _tokens = [0, 0];
    private readonly long[] _updated = [start, start];
    private long _jitterEpoch = start;
    private double _delay = 60;
    private int _cursor;
    internal int Count { get; private set; }
    internal long Bytes { get; private set; }
    internal long ReorderedPairs { get; private set; }
    internal double CorrelatedDelayMs => _delay;
    internal static bool Handles(DiagnosticMode mode) => mode is DiagnosticMode.InboundBandwidthShaping or
        DiagnosticMode.CorrelatedInboundJitter or DiagnosticMode.PacketSizeAwareShaping or
        DiagnosticMode.AsymmetricDuplexShaping or DiagnosticMode.ControlledPacketReordering;
    internal static long T(int ms) => (long)ms * Stopwatch.Frequency / 1000;
    internal static int SizeClass(uint bytes) => bytes <= 256 ? 0 : bytes <= 768 ? 1 : 2;

    internal bool Enqueue(ThrottledPacket packet, PulseShaper.Flow flow, long now)
    {
        if (Count >= 512 || packet.Length is 0 or > 1536 || Bytes + packet.Length > 512 * 1536) return false;
        if (!_lanes.TryGetValue(flow, out var lane))
        {
            if (_lanes.Count >= 128) return false;
            _lanes.Add(flow, lane = new Lane());
            _order.Add(flow);
        }
        if (mode == DiagnosticMode.CorrelatedInboundJitter)
        {
            // One AR(1) sample per elapsed 20ms epoch, NOT independent jitter per packet.
            // Bounded to the <=800ms pulse; a delayed worker cannot run an unbounded loop.
            int epochs = (int)Math.Min(40, Math.Max(0, now - _jitterEpoch) / Math.Max(1, T(20)));
            for (int i = 0; i < epochs; i++) _delay = Math.Clamp(60 + .85 * (_delay - 60) + (_random.NextDouble() * 2 - 1) * 12, 20, 100);
            _jitterEpoch += epochs * T(20);
            packet.ReleaseAtTicks = Math.Max(now + T((int)Math.Round(_delay)), lane.Packets.Count == 0 ? 0 : lane.Packets[^1].ReleaseAtTicks);
        }
        else packet.ReleaseAtTicks = now + T(mode == DiagnosticMode.ControlledPacketReordering ? 20 : 0);
        lane.Packets.Add(packet); Count++; Bytes += packet.Length;
        return true;
    }

    internal bool TryDequeue(long now, out ThrottledPacket? packet)
    {
        packet = null;
        if (Count == 0) return false;
        for (int scanned = 0; scanned < _order.Count; scanned++)
        {
            var lane = _lanes[_order[_cursor]];
            _cursor = (_cursor + 1) % _order.Count;
            if (lane.Packets.Count == 0) continue;
            var head = lane.Packets[0];
            int index = 0;
            if (mode == DiagnosticMode.ControlledPacketReordering)
            {
                // Only adjacent pairs. A singleton ages out in 20ms; never waits for future traffic.
                if (lane.CompletePair) lane.CompletePair = false;
                else if (lane.Packets.Count >= 2 && now < head.ReleaseAtTicks)
                { index = 1; lane.CompletePair = true; ReorderedPairs++; }
                else if (now < head.ReleaseAtTicks) continue;
            }
            else if (mode == DiagnosticMode.CorrelatedInboundJitter)
            {
                if (now < head.ReleaseAtTicks) continue;
            }
            else if (mode == DiagnosticMode.PacketSizeAwareShaping)
            {
                if (now < lane.Next) continue;
                int sizeClass = SizeClass(head.Length);
                int rate = sizeClass == 0 ? 32768 : sizeClass == 1 ? 16384 : 8192;
                lane.Next = now + Math.Max(T(sizeClass == 0 ? 4 : sizeClass == 1 ? 12 : 24), (long)Math.Ceiling(head.Length * (double)Stopwatch.Frequency / rate));
            }
            else
            {
                // Release metadata must retain the native address; logical direction is in flow key.
                int direction = _order[(_cursor + _order.Count - 1) % _order.Count].Source.StartsWith("in:", StringComparison.Ordinal) ? 0 : 1;
                long monotonic = Math.Max(now, _updated[direction]);
                int rate = mode == DiagnosticMode.InboundBandwidthShaping ? 16384 : direction == 0 ? 8192 : 32768;
                _tokens[direction] = Math.Min(1536, _tokens[direction] + (monotonic - _updated[direction]) * (double)rate / Stopwatch.Frequency);
                _updated[direction] = monotonic;
                // Duplex output is open 20ms in every 60ms, unlike continuous inbound pacing.
                if (mode == DiagnosticMode.AsymmetricDuplexShaping && direction == 1 && (now - _start) % T(60) >= T(20)) continue;
                if (_tokens[direction] < head.Length) continue;
                _tokens[direction] -= head.Length;
            }
            packet = lane.Packets[index]; lane.Packets.RemoveAt(index);
            Count--; Bytes -= packet.Length;
            return true;
        }
        return false;
    }

    internal IEnumerable<ThrottledPacket> Drain()
    {
        // STOP/timeout cancels pending swaps. Native send is synchronous under engine lock;
        // no scheduled old packet can be emitted after STOP returns.
        foreach (var flow in _order)
            foreach (var packet in _lanes[flow].Packets) yield return packet;
        _lanes.Clear(); _order.Clear(); Count = 0; Bytes = 0; _cursor = 0;
    }
}
