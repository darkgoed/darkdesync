using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace DarkDesync.Core;

// Stable IDs independent from DiagnosticMode. No migration or global pulse integration.
public enum NetworkExperiment { TcpReceiveWindow = 0, IPv6FlowLabel = 1, IcmpFeedback = 2, NicOffload = 3, UdpNatLifetime = 4, TcpSackTimestamp = 5 }
public enum ExperimentStatus { Operational, ObservationOnly, RequiresLab, Unavailable }
public sealed record ExperimentDefinition(NetworkExperiment Id, string Name, string Dependency, string Limitation)
{
    public override string ToString() => Name;
}
public sealed record ExperimentReport(NetworkExperiment Id, ExperimentStatus Status, string Summary, object Evidence, string Limitation);
internal sealed record NatLabEndpoint(IPAddress Address, int Port, bool OwnershipConfirmed, string Token);

internal static class NetworkExperiments
{
    internal static IReadOnlyList<ExperimentDefinition> Catalog { get; } = new[]
    {
        new ExperimentDefinition(NetworkExperiment.TcpReceiveWindow, "TCP Receive Window Clamping", "Windows + WinDivert administrador; TCP loopback próprio", "Somente observação. Não contrai janela, não altera ACK nem cria zero-window. Não atua em UDP."),
        new ExperimentDefinition(NetworkExperiment.IPv6FlowLabel, "IPv6 Flow Label Variation", "IPv6 loopback + WinDivert administrador", "A/B de 20 bits em UDP próprio; label consistente por fluxo/fase. Não comprova mudança de rota."),
        new ExperimentDefinition(NetworkExperiment.IcmpFeedback, "ICMP Control-Plane Feedback", "Pilha ICMPv4/v6 local", "Echo legítimo local; sem erros falsos. PTB/NDP e recuperação PMTU exigem roteador de laboratório."),
        new ExperimentDefinition(NetworkExperiment.NicOffload, "NIC Offload Variation", "Windows PowerShell + módulo NetAdapter", "Read-only: estado configurado não prova uso no hardware. Nenhum toggle ou placa desativada."),
        new ExperimentDefinition(NetworkExperiment.UdpNatLifetime, "UDP NAT Mapping Lifetime", "Gateway/NAT + reflector próprio tools/nat_reflector.py + token", "Sondas atrasadas sem keepalive medem sobrevivência, não timeout exato; loopback não prova NAT externo."),
        new ExperimentDefinition(NetworkExperiment.TcpSackTimestamp, "TCP SACK / Timestamp Behavior", "Windows + WinDivert administrador; TCP loopback próprio", "Somente opções realmente capturadas. Ausência de SACK blocks/timestamps é registrada, não fabricada. Sem benefício UDP.")
    };
    internal static string StatusLabel(ExperimentStatus status) => status switch
    {
        ExperimentStatus.Operational => "Operacional (ensaio próprio)",
        ExperimentStatus.ObservationOnly => "Somente observação",
        ExperimentStatus.RequiresLab => "Requer laboratório",
        _ => "Indisponível"
    };

    internal static async Task<ExperimentReport> RunAsync(NetworkExperiment id, NatLabEndpoint? nat, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(id)) throw new ArgumentOutOfRangeException(nameof(id));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            return id switch
            {
                NetworkExperiment.TcpReceiveWindow or NetworkExperiment.TcpSackTimestamp => await TcpAsync(id, timeout.Token),
                NetworkExperiment.IPv6FlowLabel => await FlowLabelAsync(timeout.Token),
                NetworkExperiment.IcmpFeedback => await IcmpAsync(timeout.Token),
                NetworkExperiment.NicOffload => await OffloadAsync(timeout.Token),
                NetworkExperiment.UdpNatLifetime => nat == null
                    ? Report(id, ExperimentStatus.RequiresLab, "Nenhum reflector próprio configurado; nenhuma sonda enviada.", new { Sent = 0 })
                    : await NatAsync(nat, timeout.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(id))
            };
        }
        catch (OperationCanceledException)
        {
            return Report(id, ExperimentStatus.Unavailable, cancellationToken.IsCancellationRequested ? "STOP: ensaio cancelado; recursos liberados." : "Prazo de 25 s excedido; ensaio não comprovado.", new { Cancelled = true });
        }
        catch (Exception ex) when (ex is SocketException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or
            System.Net.NetworkInformation.PingException or System.IO.IOException or InvalidOperationException or JsonException or ArgumentException or DllNotFoundException or TimeoutException)
        { return Report(id, ExperimentStatus.Unavailable, ex.GetType().Name + ": " + ex.Message, new { Error = ex.HResult }); }
    }
    private static ExperimentReport Report(NetworkExperiment id, ExperimentStatus status, string summary, object evidence) =>
        new(id, status, summary, evidence, Catalog.Single(x => x.Id == id).Limitation);

    private static async Task<ExperimentReport> TcpAsync(NetworkExperiment id, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WinDivert requer Windows.");
        var all = new List<object>();
        foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            { all.Add(new { Family = "IPv6", Available = false }); continue; }
            var listener = new TcpListener(ip, 0); listener.Start();
            try
            {
                using var client = new TcpClient(ip.AddressFamily); client.Client.Bind(new IPEndPoint(ip, 0));
                int src = ((IPEndPoint)client.Client.LocalEndPoint!).Port, dst = ((IPEndPoint)listener.LocalEndpoint).Port;
                using var capture = new ExperimentCapture($"outbound and loopback and {(ip.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ip")} and tcp and ((tcp.SrcPort == {src} and tcp.DstPort == {dst}) or (tcp.SrcPort == {dst} and tcp.DstPort == {src}))");
                await client.ConnectAsync(ip, dst, ct);
                using var server = await listener.AcceptTcpClientAsync(ct);
                byte[] sent = RandomNumberGenerator.GetBytes(8192), received = new byte[sent.Length];
                // A/B is application direction, NOT a window clamp or fabricated SACK.
                await client.GetStream().WriteAsync(sent, ct); await server.GetStream().ReadExactlyAsync(received, ct);
                if (!received.SequenceEqual(sent)) throw new InvalidOperationException("TCP payload mismatch");
                await server.GetStream().WriteAsync(sent, ct); await client.GetStream().ReadExactlyAsync(received, ct);
                if (!received.SequenceEqual(sent)) throw new InvalidOperationException("TCP echo mismatch");
                await Task.Delay(150, ct); capture.Dispose();
                var observed = capture.Packets.Select(p => NetworkExperimentPackets.TryTcp(p, out var o) ? o : null).Where(o => o != null).Select(o => o!).ToArray();
                bool negotiated = observed.Any(o => o.Syn && !o.Ack && o.Options.WindowScale.HasValue) && observed.Any(o => o.Syn && o.Ack && o.Options.WindowScale.HasValue);
                var windows = observed.Select(o => new { o.Flow, o.Syn, o.Window, ScaleNegotiated = negotiated,
                    EffectiveBytes = o.Syn || !negotiated ? (long?)o.Window : (long?)o.Window << observed.First(s => s.Syn && s.Flow == o.Flow).Options.WindowScale!.Value }).ToArray();
                if (capture.ReadError != 0 || !observed.Any(o => o.Syn && !o.Ack) || !observed.Any(o => o.Syn && o.Ack))
                    throw new InvalidOperationException($"Handshake não capturado integralmente; Win32={capture.ReadError}");
                all.Add(new { Family = ip.AddressFamily.ToString(), SourcePort = src, DestinationPort = dst, PayloadExact = true, Observations = observed, Windows = windows,
                    PacketsHex = capture.Packets.Select(Convert.ToHexString).ToArray() });
            }
            finally { listener.Stop(); }
        }
        return Report(id, ExperimentStatus.ObservationOnly, "TCP próprio IPv4/IPv6: handshake, advertised window e opções reais; sem edição. Veja opções ausentes e escala negociada no JSON.", all);
    }

    private static async Task<ExperimentReport> FlowLabelAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6) throw new PlatformNotSupportedException("IPv6 loopback/WinDivert indisponível.");
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        using var source = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        int src = ((IPEndPoint)source.Client.LocalEndPoint!).Port, dst = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        using var capture = new ExperimentCapture($"outbound and loopback and ipv6 and udp and udp.SrcPort == {src} and udp.DstPort == {dst}");
        var phases = new List<object>();
        // Same 5-tuple; two stable labels in two explicitly bounded phases, not per-packet randomization.
        foreach (uint label in new uint[] { 0x12345, 0xabcde })
        {
            foreach (int sequence in new[] { 0, 1 })
            {
                ct.ThrowIfCancellationRequested();
                byte[] payload = RandomNumberGenerator.GetBytes(32);
                byte[] packet = NetworkExperimentPackets.OwnUdp6(src, dst, label, payload);
                ExperimentCapture.SendOwnUdp6(packet);
                var got = await receiver.ReceiveAsync(ct);
                if (!got.Buffer.SequenceEqual(payload) || got.RemoteEndPoint.Port != src) throw new InvalidOperationException("IPv6 payload mismatch");
                phases.Add(new { Label = label, Sequence = sequence, PayloadExact = true, SentHex = Convert.ToHexString(packet) });
            }
        }
        await Task.Delay(100, ct); capture.Dispose();
        var wire = capture.Packets.ToArray();
        if (capture.ReadError != 0 || wire.Length != 4 || wire.Count(p => NetworkExperimentPackets.FlowLabel(p) == 0x12345) != 2 || wire.Count(p => NetworkExperimentPackets.FlowLabel(p) == 0xabcde) != 2)
            throw new InvalidOperationException("A/B flow labels não comprovados na captura.");
        return Report(NetworkExperiment.IPv6FlowLabel, ExperimentStatus.Operational, "A/B: dois labels de 20 bits, dois datagramas por fase, mesmo fluxo próprio; recebimento e captura confirmados.",
            new { SourcePort = src, DestinationPort = dst, Phases = phases, CapturedHex = wire.Select(Convert.ToHexString).ToArray() });
    }

    private static async Task<ExperimentReport> IcmpAsync(CancellationToken ct)
    {
        var probes = new List<object>(); bool success = true;
        foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            ct.ThrowIfCancellationRequested();
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            { probes.Add(new { Address = ip.ToString(), Status = "IPv6 indisponível" }); success = false; continue; }
            using var ping = new Ping(); byte[] token = RandomNumberGenerator.GetBytes(32);
            var reply = await ping.SendPingAsync(ip, TimeSpan.FromSeconds(2), token, null, ct);
            bool exact = reply.Status == IPStatus.Success && reply.Address.Equals(ip) && reply.Buffer.SequenceEqual(token);
            success &= exact;
            probes.Add(new { Address = ip.ToString(), Status = reply.Status.ToString(), reply.RoundtripTime, PayloadExact = exact,
                RequestType = ip.AddressFamily == AddressFamily.InterNetwork ? 8 : 128, ReplyType = ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 129,
                Source = "Ping API: echo types defined by protocol, not raw packet capture; no PTB/NDP observation" });
        }
        return Report(NetworkExperiment.IcmpFeedback, success ? ExperimentStatus.ObservationOnly : ExperimentStatus.Unavailable,
            "Echo ICMPv4/v6 legítimo próprio; API reporta status/RTT/payload. Erros de roteador, PTB, NDP e recuperação não ensaiados.", probes);
    }

    private static async Task<ExperimentReport> OffloadAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NetAdapter requer Windows.");
        const string script = "$ErrorActionPreference='Stop'; $names=@('Get-NetAdapter','Get-NetAdapterUso','Get-NetAdapterLso','Get-NetAdapterChecksumOffload','Get-NetAdapterRss'); $r=@(); foreach($n in $names) { try { $v=@(& $n -ErrorAction Stop | Select-Object Name,InterfaceDescription,Status,Enabled,IPv4Enabled,IPv6Enabled,IPv4Transmit,IPv4Receive,IPv6Transmit,IPv6Receive,TcpIPv4Enabled,TcpIPv6Enabled,UdpIPv4Enabled,UdpIPv6Enabled,NumberOfReceiveQueues); $r+=@{Command=$n; Available=$true; Values=$v} } catch { $r+=@{Command=$n; Available=$false; Error=$_.Exception.Message} } }; ConvertTo-Json -Depth 6 -InputObject $r -Compress";
        using var process = new Process { StartInfo = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        using var kill = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct); string text = await stdout, error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("NetAdapter read failed: " + error);
        var data = JsonSerializer.Deserialize<JsonElement>(text);
        bool available = data.EnumerateArray().Any(x => x.GetProperty("Available").GetBoolean() && x.GetProperty("Values").GetArrayLength() > 0 && x.GetProperty("Command").GetString() != "Get-NetAdapter");
        return Report(NetworkExperiment.NicOffload, available ? ExperimentStatus.ObservationOnly : ExperimentStatus.Unavailable,
            "USO/LSO/checksum/RSS: consultas read-only por adaptador; falhas individuais explícitas. Capacidade/configuração não prova uso efetivo; correlação de hardware não realizada.", data);
    }

    internal static void ValidateNat(NatLabEndpoint lab)
    {
        byte[] bytes = lab.Address.GetAddressBytes();
        bool multicast = lab.Address.AddressFamily == AddressFamily.InterNetworkV6 ? lab.Address.IsIPv6Multicast : bytes[0] >= 224;
        if (!lab.OwnershipConfirmed || lab.Port is < 1024 or > 65535 || lab.Address.Equals(IPAddress.Any) || lab.Address.Equals(IPAddress.IPv6Any) ||
            lab.Address.IsIPv4MappedToIPv6 || multicast || lab.Token.Length is < 16 or > 128 || lab.Token.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Confirme reflector próprio, IP unicast numérico, porta 1024–65535 e token alfanumérico 16–128 caracteres.");
    }

    private static async Task<ExperimentReport> NatAsync(NatLabEndpoint lab, CancellationToken ct)
    {
        ValidateNat(lab); var trials = new List<object>(); bool delayed = false; bool external = false;
        foreach (int idle in new[] { 1, 3, 6 })
        {
            ct.ThrowIfCancellationRequested();
            using var socket = new UdpClient(lab.Address.AddressFamily); socket.Connect(lab.Address, lab.Port);
            string nonce = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.SerializeToUtf8Bytes(new { token = lab.Token, nonce, idle });
            await socket.SendAsync(request, ct);
            async Task<JsonElement> Receive(string phase)
            {
                var result = await socket.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(idle + 2), ct);
                if (result.Buffer.Length > 1024) throw new InvalidOperationException("Reflector response too large");
                var json = JsonSerializer.Deserialize<JsonElement>(result.Buffer);
                if (json.GetProperty("nonce").GetString() != nonce || json.GetProperty("phase").GetString() != phase)
                    throw new InvalidOperationException("Reflector nonce/phase mismatch");
                return json;
            }
            var initial = await Receive("baseline");
            string mapped = initial.GetProperty("address").GetString()!; int port = initial.GetProperty("port").GetInt32();
            bool translated = mapped != ((IPEndPoint)socket.Client.LocalEndPoint!).Address.ToString() || port != ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            external |= translated; bool survived;
            try { var after = await Receive("delayed"); survived = after.GetProperty("address").GetString() == mapped && after.GetProperty("port").GetInt32() == port; }
            catch (TimeoutException) { survived = false; }
            delayed |= survived;
            trials.Add(new { IdleSeconds = idle, MappedAddress = mapped, MappedPort = port, TranslationObserved = translated, DelayedInboundReceived = survived, ClientPacketsSent = 1,
                Interpretation = survived ? "Sobrevivência >= intervalo; não é timeout exato" : "Inconclusivo: perda/filtro/timeout; expiração não provada" });
        }
        return Report(NetworkExperiment.UdpNatLifetime, delayed && external ? ExperimentStatus.Operational : ExperimentStatus.RequiresLab,
            external ? "Reflexão própria medida; sobrevivência por inbound atrasado sem keepalive. Sem prova de expiração exata." : "Protocolo reflexivo executado, mas sem tradução observada: não comprova NAT externo.", trials);
    }
}
