using System.Text;
using Microsoft.Extensions.Logging;

namespace DarkDesync.Core;

public enum FilterMode
{
    /// <summary>
    /// Intercepts all outgoing packets (WinDivert "outbound").
    /// Matches Clumsy's "all sending packets" preset.
    /// Recommended for network testing and multiplayer desync simulation.
    /// </summary>
    Outbound,

    /// <summary>
    /// Intercepts all network traffic, both incoming and outgoing (WinDivert "true").
    /// </summary>
    AllTraffic,

    /// <summary>
    /// Filters by the active ports discovered for the target application.
    /// If no ports are detected, automatically falls back to Outbound.
    /// </summary>
    ProcessPorts,

    /// <summary>
    /// Custom WinDivert filter expression entered by the user.
    /// </summary>
    Custom,

    /// <summary>
    /// Restricts strictly to authorized test endpoints and/or target application ports.
    /// </summary>
    AuthorizedEndpoint
}

/// <summary>
/// Constructs and validates WinDivert filter expressions based on the chosen mode,
/// discovered process ports, or custom filter strings.
/// </summary>
public sealed class FilterBuilder
{
    private readonly ILogger<FilterBuilder> _logger;
    private const int MaxPortClauses = 50;

    public FilterBuilder(ILogger<FilterBuilder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generates a validated WinDivert filter expression.
    /// </summary>
    public string GetFilter(
        FilterMode mode,
        HashSet<ushort>? localPorts = null,
        string? customExpression = null,
        string? authorizedEndpoint = null)
    {
        string rawFilter = mode switch
        {
            FilterMode.Outbound => "outbound",
            FilterMode.AllTraffic => "true",
            FilterMode.ProcessPorts => (localPorts != null && localPorts.Count > 0)
                ? BuildPortFilter(localPorts)
                : "outbound", // Fallback to outbound when no listening ports are found
            FilterMode.Custom => !string.IsNullOrWhiteSpace(customExpression)
                ? customExpression.Trim()
                : "outbound",
            FilterMode.AuthorizedEndpoint => BuildAuthorizedFilter(authorizedEndpoint, localPorts),
            _ => "outbound"
        };

        // Validate the expression with WinDivert
        if (!WinDivertNative.ValidateFilter(rawFilter, WinDivertNative.Layer.Network, out string? error))
        {
            _logger.LogWarning("Filter '{Raw}' is invalid ({Error}). Falling back to 'outbound'.", rawFilter, error);
            return "outbound";
        }

        return rawFilter;
    }

    /// <summary>
    /// Builds a WinDivert filter restricted strictly to authorized test traffic
    /// (authorized endpoint and/or discovered process ports), preserving third-party services.
    /// </summary>
    public string BuildAuthorizedFilter(string? authorizedEndpoint, HashSet<ushort>? localPorts = null)
    {
        var sb = new StringBuilder();
        string endpoint = (authorizedEndpoint ?? "127.0.0.1").Trim();
        bool hasProcessPorts = localPorts != null && localPorts.Count > 0;

        if (endpoint.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("(loopback or (ip and (ip.SrcAddr == 127.0.0.1 or ip.DstAddr == 127.0.0.1)))");
        }
        else if (endpoint.Contains(':'))
        {
            var parts = endpoint.Split(':');
            if (parts.Length == 2 && ushort.TryParse(parts[1], out ushort port))
            {
                string ipPart = parts[0];
                if (!string.IsNullOrEmpty(ipPart) && System.Net.IPAddress.TryParse(ipPart, out _))
                {
                    sb.Append($"(ip and (ip.SrcAddr == {ipPart} or ip.DstAddr == {ipPart}) and (tcp.SrcPort == {port} or tcp.DstPort == {port} or udp.SrcPort == {port} or udp.DstPort == {port}))");
                }
                else
                {
                    sb.Append($"(tcp.SrcPort == {port} or tcp.DstPort == {port} or udp.SrcPort == {port} or udp.DstPort == {port})");
                }
            }
            else
            {
                sb.Append("(loopback or (ip and (ip.SrcAddr == 127.0.0.1 or ip.DstAddr == 127.0.0.1)))");
            }
        }
        else if (System.Net.IPAddress.TryParse(endpoint, out _))
        {
            sb.Append($"(ip and (ip.SrcAddr == {endpoint} or ip.DstAddr == {endpoint}))");
        }
        else
        {
            sb.Append("(loopback or (ip and (ip.SrcAddr == 127.0.0.1 or ip.DstAddr == 127.0.0.1)))");
        }

        if (hasProcessPorts)
        {
            var ports = localPorts!.Take(MaxPortClauses).ToList();
            sb.Append(" or (");
            for (int i = 0; i < ports.Count; i++)
            {
                if (i > 0) sb.Append(" or ");
                ushort p = ports[i];
                sb.Append($"tcp.SrcPort == {p} or tcp.DstPort == {p} or udp.SrcPort == {p} or udp.DstPort == {p}");
            }
            sb.Append(')');
        }

        string filter = sb.ToString();
        if (!WinDivertNative.ValidateFilter(filter, WinDivertNative.Layer.Network, out _))
        {
            return "loopback";
        }

        return filter;
    }

    /// <summary>
    /// Builds a port-specific WinDivert filter.
    /// </summary>
    public string BuildPortFilter(HashSet<ushort> localPorts)
    {
        if (localPorts.Count == 0)
            return "outbound";

        var ports = localPorts.Take(MaxPortClauses).ToList();
        var sb = new StringBuilder();

        sb.Append("outbound and (");
        for (int i = 0; i < ports.Count; i++)
        {
            if (i > 0)
                sb.Append(" or ");

            ushort port = ports[i];
            sb.Append($"tcp.SrcPort == {port} or tcp.DstPort == {port} or udp.SrcPort == {port} or udp.DstPort == {port}");
        }
        sb.Append(')');

        string filter = sb.ToString();
        if (!WinDivertNative.ValidateFilter(filter, WinDivertNative.Layer.Network, out _))
        {
            return "outbound";
        }

        return filter;
    }

    /// <summary>
    /// Legacy compatibility helper.
    /// </summary>
    public string BuildFilter(HashSet<ushort> localPorts)
    {
        return BuildPortFilter(localPorts);
    }

    public static string BuildAllTrafficFilter() => "true";
    public static string BuildOutboundFilter() => "outbound";
}
