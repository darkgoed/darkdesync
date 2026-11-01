using DarkDesync.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public class FilterBuilderTests
{
    private readonly FilterBuilder _builder;

    public FilterBuilderTests()
    {
        _builder = new FilterBuilder(NullLogger<FilterBuilder>.Instance);
    }

    [Fact]
    public void BuildFilter_WithNoPorts_FailsClosed()
    {
        var filter = _builder.BuildFilter(new HashSet<ushort>());
        Assert.Equal("false", filter);
    }

    [Fact]
    public void GetFilter_WithOutboundMode_ReturnsOutbound()
    {
        var filter = _builder.GetFilter(FilterMode.Outbound);
        Assert.Equal("outbound", filter);
    }

    [Fact]
    public void BuildFilter_WithSinglePort_ContainsTcpAndUdpClauses()
    {
        var ports = new HashSet<ushort> { 8080 };
        var filter = _builder.BuildFilter(ports);

        Assert.Contains("tcp.SrcPort == 8080", filter);
        Assert.Contains("tcp.DstPort == 8080", filter);
        Assert.Contains("udp.SrcPort == 8080", filter);
        Assert.Contains("udp.DstPort == 8080", filter);
    }

    [Fact]
    public void BuildFilter_WithMultiplePorts_ConnectsWithOr()
    {
        var ports = new HashSet<ushort> { 80, 443 };
        var filter = _builder.BuildFilter(ports);

        Assert.Contains("tcp.SrcPort == 80", filter);
        Assert.Contains("tcp.SrcPort == 443", filter);
        Assert.Contains(" or ", filter);
    }

    [Fact]
    public void BuildAllTrafficFilter_ReturnsTrue()
    {
        var filter = FilterBuilder.BuildAllTrafficFilter();
        Assert.Equal("true", filter);
    }

    [Fact]
    public void BuildFilter_WithExcessivePorts_FailsClosedRatherThanTruncating()
    {
        var ports = new HashSet<ushort>();
        for (ushort i = 1000; i < 1100; i++)
        {
            ports.Add(i);
        }

        var filter = _builder.BuildFilter(ports);
        Assert.Equal("false", filter);
    }

    [Fact]
    public void ScopedFilter_NeverLosesExactTupleToBroadCustomRestriction()
    {
        string filter = FilterBuilder.BuildScopedFilter([DiagnosticModesTests.Endpoint()], "false or true");
        Assert.StartsWith("ip and tcp and (", filter);
        Assert.Contains("ip.SrcAddr == 127.0.0.1 and tcp.SrcPort == 40000", filter);
        Assert.Contains("ip.DstAddr == 127.0.0.2 and tcp.DstPort == 443", filter);
        Assert.EndsWith("and (false or true)", filter);
        Assert.Equal("false", FilterBuilder.BuildScopedFilter([], "true"));
        Assert.Equal("false", FilterBuilder.BuildScopedFilter([DiagnosticModesTests.Endpoint() with { Protocol = 17 }], "true"));
        Assert.Equal("false", FilterBuilder.BuildScopedFilter(Enumerable.Range(1, 65).Select(p => DiagnosticModesTests.Endpoint((ushort)p)), "true"));
    }
}
