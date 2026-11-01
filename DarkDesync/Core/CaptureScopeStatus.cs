using System.Net;

namespace DarkDesync.Core;

/// <summary>Immutable, atomically published control-plane snapshot; not a claim of game-channel detection.</summary>
public sealed record CaptureScopeStatus(uint ProcessId, bool Observing, bool Requested, bool Running,
    int TcpObserved, int UdpBindings, int UdpObserved, int UdpEligible,
    int TcpScoped, int UdpScoped, string Reason, string Filter, string Tuples)
{
    public static CaptureScopeStatus Empty { get; } = new(0, false, false, false, 0, 0, 0, 0, 0, 0,
        "Selecione o alvo e ative observação antes de entrar na experiência. UDP antigo não é reconstruído.", "false", "");

    internal static string Describe(IEnumerable<ProcessNetworkScanner.ProcessEndpoint> endpoints) =>
        string.Join(Environment.NewLine, endpoints.Select(e =>
            $"{(e.Protocol == 6 ? "TCP" : "UDP")} {new IPAddress(e.LocalAddr)}:{e.LocalPort} ↔ {new IPAddress(e.RemoteAddr)}:{e.RemotePort}"));
}
