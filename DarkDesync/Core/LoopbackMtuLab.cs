using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DarkDesync.Core;

public sealed record MtuProbe(int PayloadBytes, bool DontFragment, bool ReceivedExact, string Outcome, double ElapsedMs);
public sealed record MtuLabReport(string Id, int? InterfaceMtu, int LargestDeliveredIpBytes, IReadOnlyList<MtuProbe> Probes,
    bool FragmentationProven, bool Completed, string Limitation, int SourcePort = 0, int DestinationPort = 0);

// Only loopback endpoints owned by this process. Never edits host MTU, routes,
// registry, firewall or IPv6 PMTUD, and never crafts/injects fragments globally.
internal static class LoopbackMtuLab
{
    internal static async Task<MtuLabReport> RunAsync(CancellationToken cancellationToken)
    {
        int? mtu = null;
        try
        {
            mtu = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                .Select(n => (int?)n.GetIPProperties().GetIPv4Properties()?.Mtu).FirstOrDefault(m => m > 0);
        }
        catch (NetworkInformationException) { }
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        sender.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
        var probes = new List<MtuProbe>(); int delivered = 0;
        bool completed = false;
        try
        {
          foreach (bool df in new[] { true, false })
          foreach (int size in new[] { 128, 1472, 4000, 60000 })
          {
            cancellationToken.ThrowIfCancellationRequested(); sender.DontFragment = df;
            byte[] bytes = new byte[size]; for (int i = 0; i < size; i++) bytes[i] = (byte)(i * 31 + size);
            var clock = Stopwatch.StartNew(); string outcome; bool exact = false;
            try
            {
                await sender.SendAsync(bytes, cancellationToken);
                var read = await receiver.ReceiveAsync(cancellationToken);
                exact = read.RemoteEndPoint.Equals(sender.Client.LocalEndPoint) &&
                    CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(read.Buffer));
                outcome = exact ? "DeliveredExact" : "PayloadMismatch";
                if (exact) delivered = Math.Max(delivered, size + 28);
            }
            catch (SocketException ex) { outcome = ex.SocketErrorCode.ToString(); }
            probes.Add(new(size, df, exact, outcome, clock.Elapsed.TotalMilliseconds));
          }
          completed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        return new(Guid.NewGuid().ToString("N"), mtu, delivered, probes, false, completed,
            "Ensaio IPv4/UDP próprio em loopback: MTU lido, DF solicitado ligado/desligado e payload verificado. Maior entrega é limite inferior, não PMTU WAN. Sem prova de fragmentação/reassemblagem por WinDivert; não há fragmentação global nem alteração persistente. IPv6 PMTUD não é modificado.",
            ((IPEndPoint)sender.Client.LocalEndPoint!).Port, ((IPEndPoint)receiver.Client.LocalEndPoint!).Port);
    }
}
