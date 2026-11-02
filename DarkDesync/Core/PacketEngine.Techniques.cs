using System.Runtime.InteropServices;

namespace DarkDesync.Core;

public sealed partial class PacketEngine
{
    private void ResetTechniqueCounters()
    {
        Interlocked.Exchange(ref _duplicateCopiesSent, 0); Interlocked.Exchange(ref _ecnMarked, 0);
        Interlocked.Exchange(ref _dscpMarked, 0); Interlocked.Exchange(ref _acksHeld, 0); Interlocked.Exchange(ref _acksReleased, 0);
    }
    private bool ProcessTechniquePacket(IntPtr buffer, uint length, WinDivertNative.Address native)
    {
        // Called only under packet lock and a live, manually triggered pulse.
        if (!native.Outbound || native.Impostor || length is < 20 or > 4096 || _activeDiagnosticMode == DiagnosticMode.MtuFragmentationLab)
        { RecordSend(SendPacket(buffer, length, native)); return false; }
        byte[] bytes = new byte[length]; Marshal.Copy(buffer, bytes, 0, (int)length);
        if (!TechniquePackets.TryParse(bytes, out var layout))
        { RecordSend(SendPacket(buffer, length, native)); return false; }
        if (_activeDiagnosticMode == DiagnosticMode.PacketDuplication)
        {
            bool sent = SendPacket(buffer, length, native); RecordSend(sent);
            // Original first; one immediate optional UDP copy. Never queue copies,
            // duplicate TCP, change TTL/sequence/address, or recursively reinject.
            if (sent && layout.Protocol == 17 && length <= 1400 && _duplicationBudget < 16 &&
                _duplicationBytes + length <= 32768 && !_stopping && _timestamp() < _effectDeadline)
            {
                _duplicationBudget++; _duplicationBytes += (int)length;
                if (SendPacket(buffer, length, native)) Interlocked.Increment(ref _duplicateCopiesSent);
                else
                {
                    Interlocked.Increment(ref _reinjectionFailures);
                    RequestSafetyStop("Cópia UDP opcional falhou; original já enviado, parada por segurança.");
                }
            }
            return false;
        }
        if (_activeDiagnosticMode is DiagnosticMode.EcnCongestionMarking or DiagnosticMode.DscpTrafficMarking)
        {
            if (!TechniquePackets.TryMark(bytes, _activeDiagnosticMode, _pulseDscp, out var modified))
            { RecordSend(SendPacket(buffer, length, native)); return false; }
            // Only checksum-valid flags change; direction/interface/timestamps
            // remain from the capture. No checksum offload assumptions required.
            if (!layout.IPv6) native.Flags |= 1u << 21;
            native.Flags |= 1u << (layout.Protocol == 6 ? 22 : 23);
            IntPtr copy = Marshal.AllocHGlobal(modified!.Length);
            try
            {
                Marshal.Copy(modified, 0, copy, modified.Length);
                bool success = SendPacket(copy, length, native); RecordSend(success);
                if (success)
                {
                    if (_activeDiagnosticMode == DiagnosticMode.EcnCongestionMarking) Interlocked.Increment(ref _ecnMarked);
                    else Interlocked.Increment(ref _dscpMarked);
                }
            }
            finally { Marshal.FreeHGlobal(copy); }
            return false;
        }
        if (_activeDiagnosticMode == DiagnosticMode.TcpAckPacing && layout.Protocol == 6)
        {
            var flow = TechniquePackets.Flow(bytes, layout);
            if (!layout.PureAck)
            {
                // Data/control packets must not overtake ACKs already retained
                // for this tuple; piggyback ACKs are never treated as pure ACKs.
                if (_ackQueue != null)
                    foreach (var ack in _ackQueue.DrainFlow(flow)) { SendThrottledPacket(ack); Interlocked.Increment(ref _acksReleased); }
            }
            else if (_ackQueue != null)
            {
                var packet = new ThrottledPacket { Data = bytes, Length = length, Address = native, TimestampTicks = _timestamp() };
                if (_ackQueue.Enqueue(flow, packet, packet.TimestampTicks))
                { Interlocked.Increment(ref _acksHeld); Interlocked.Increment(ref _packetsThrottled); return true; }
                LastShapingFallback = "ACK: fila/bytes/fluxos no limite; pulso cancelado e drenado (fail-open).";
                CancelArmedEffectLocked();
            }
        }
        RecordSend(SendPacket(buffer, length, native)); return false;
    }

    private void StartMtuLabLocked(long generation)
    {
        // Prior worker must be complete before its CTS can be disposed/replaced.
        _mtuCancellation?.Dispose();
        var cancellation = _mtuCancellation = new CancellationTokenSource();
        int duration = (int)Math.Clamp((_effectDeadline - _effectStartedTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency, 1, 800);
        cancellation.CancelAfter(duration); _lastMtuReport = null;
        _mtuTask = Task.Run(async () =>
        {
            MtuLabReport report;
            try { report = await LoopbackMtuLab.RunAsync(cancellation.Token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                report = new(generation.ToString(), null, 0, Array.Empty<MtuProbe>(), false, false,
                    "Laboratório local indisponível: " + ex.GetType().Name + "; nenhum MTU/rota/firewall foi alterado.");
            }
            lock (_packetLock)
            {
                if (ReferenceEquals(_mtuCancellation, cancellation)) _lastMtuReport = report;
            }
        });
    }
}
