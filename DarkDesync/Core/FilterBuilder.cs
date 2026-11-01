using System.Net;
using Microsoft.Extensions.Logging;

namespace DarkDesync.Core;

// Strict modes intersect verified PID tuples. Global legacy is a separate, consented pulse-only entry.
public enum FilterMode { Outbound, AllTraffic, ProcessPorts, Custom, LegacyGlobalOutbound }

public sealed class FilterBuilder
{
    private readonly ILogger<FilterBuilder> _logger;
    public FilterBuilder(ILogger<FilterBuilder> logger) => _logger = logger;

    public string GetFilter(FilterMode mode, HashSet<ushort>? localPorts = null, string? customExpression = null)
    {
        string raw = mode switch
        {
            FilterMode.Outbound => "outbound",
            FilterMode.AllTraffic => "true",
            FilterMode.ProcessPorts => localPorts == null ? "false" : BuildPortFilter(localPorts),
            FilterMode.Custom => string.IsNullOrWhiteSpace(customExpression) ? "false" : customExpression.Trim(),
            _ => "false"
        };
        if (!WinDivertNative.ValidateFilter(raw, WinDivertNative.Layer.Network, out var error))
        {
            _logger.LogWarning("Invalid filter: {Error}; capture blocked", error);
            return "false";
        }
        return raw;
    }

    internal static string BuildScopedFilter(IEnumerable<ProcessNetworkScanner.ProcessEndpoint> endpoints, string restriction)
    {
        var connections = endpoints.Distinct().ToArray();
        if (connections.Length == 0 || connections.Length > 64 || connections.Any(e =>
            (e.Protocol != 6 && e.Protocol != 17) || e.LocalPort == 0 || e.RemotePort == 0 || e.LocalAddr == 0 || e.RemoteAddr == 0))
            return "false";
        var clauses = connections.Select(e =>
        {
            string local = new IPAddress(e.LocalAddr).ToString();
            string remote = new IPAddress(e.RemoteAddr).ToString();
            string protocol = e.Protocol == 6 ? "tcp" : "udp";
            return $"({protocol} and ((outbound and ip.SrcAddr == {local} and {protocol}.SrcPort == {e.LocalPort} and ip.DstAddr == {remote} and {protocol}.DstPort == {e.RemotePort}) or (inbound and ip.DstAddr == {local} and {protocol}.DstPort == {e.LocalPort} and ip.SrcAddr == {remote} and {protocol}.SrcPort == {e.RemotePort})))";
        });
        // Outer scope cannot be escaped by an 'or true' in the custom restriction.
        return $"ip and ({string.Join(" or ", clauses)}) and ({restriction})";
    }

    public string BuildPortFilter(HashSet<ushort> localPorts)
    {
        if (localPorts.Count == 0 || localPorts.Count > 50 || localPorts.Contains(0)) return "false";
        return "outbound and (" + string.Join(" or ", localPorts.OrderBy(p => p).Select(p =>
            $"tcp.SrcPort == {p} or tcp.DstPort == {p} or udp.SrcPort == {p} or udp.DstPort == {p}")) + ")";
    }
    public string BuildFilter(HashSet<ushort> localPorts) => BuildPortFilter(localPorts);
    public static string BuildAllTrafficFilter() => "true";
    public static string BuildOutboundFilter() => "outbound";
}
