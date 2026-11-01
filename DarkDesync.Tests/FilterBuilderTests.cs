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
    public void BuildFilter_WithNoPorts_FallsBackToOutbound()
    {
        var filter = _builder.BuildFilter(new HashSet<ushort>());
        Assert.Equal("outbound", filter);
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
    public void BuildFilter_WithExcessivePorts_CapsToMaxPorts()
    {
        var ports = new HashSet<ushort>();
        for (ushort i = 1000; i < 1100; i++)
        {
            ports.Add(i);
        }

        var filter = _builder.BuildFilter(ports);
        Assert.NotEmpty(filter);
        Assert.NotEqual("false", filter);
    }
}
