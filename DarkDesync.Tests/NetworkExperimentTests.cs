using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DarkDesync.Core;
using Xunit;

namespace DarkDesync.Tests;

public sealed class NetworkExperimentTests
{
    [Fact]
    public void CatalogIsSeparateStableAndNeverAddsPulseModes()
    {
        Assert.Equal(Enumerable.Range(0, 6), NetworkExperiments.Catalog.Select(x => (int)x.Id));
        Assert.Equal(4, ModeCatalog.OutboundChoices.Count);
        Assert.All(NetworkExperiments.Catalog, x => { Assert.NotEmpty(x.Dependency); Assert.NotEmpty(x.Limitation); });
        Assert.Equal("Somente observação", NetworkExperiments.StatusLabel(ExperimentStatus.ObservationOnly));
        Assert.Equal("Requer laboratório", NetworkExperiments.StatusLabel(ExperimentStatus.RequiresLab));
        Assert.Equal("Indisponível", NetworkExperiments.StatusLabel(ExperimentStatus.Unavailable));
    }

    [Theory]
    [InlineData(0u)] [InlineData(0xfffffu)] [InlineData(0x12345u)]
    public void FlowLabelsPreserveAllOtherFieldsAndIndependentChecksum(uint label)
    {
        byte[] payload = Enumerable.Range(0, 31).Select(x => (byte)x).ToArray();
        byte[] a = NetworkExperimentPackets.OwnUdp6(31000, 31001, 0, payload), b = NetworkExperimentPackets.OwnUdp6(31000, 31001, label, payload);
        Assert.Equal(label, NetworkExperimentPackets.FlowLabel(b));
        Assert.Equal(a[0], b[0]); Assert.Equal(a[1] & 0xf0, b[1] & 0xf0); Assert.Equal(a.Skip(4), b.Skip(4));
        Assert.Equal(payload, b.Skip(48)); NetworkTechniquesTests.AssertChecksums(b);
        Assert.Equal(0x2a, ((b[0] & 15) << 4) | (b[1] >> 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkExperimentPackets.OwnUdp6(1, 2, 0x100000, payload));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkExperimentPackets.OwnUdp6(0, 2, 1, payload));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkExperimentPackets.OwnUdp6(1, 2, 1, new byte[1201]));
        Assert.Throws<ArgumentException>(() => NetworkExperimentPackets.FlowLabel(new byte[39]));
    }

    private static byte[] Tcp(byte[] options, bool syn = true, bool v6 = false)
    {
        int ip = v6 ? 40 : 20;
        byte[] p = NetworkTechniquesTests.Packet(v6, 6, payload: options.Length, flags: syn ? (byte)2 : (byte)16);
        p[ip + 12] = (byte)((20 + options.Length) / 4 << 4); options.CopyTo(p, ip + 20);
        NetworkTechniquesTests.RepairReference(p, v6, 6); return p;
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ParsesRealOptionLayoutWithoutInventingAbsentFields(bool v6)
    {
        byte[] options = { 3,3,7,1,4,2,1,1,8,10,0,0,0,42,0,0,0,19,0,0 };
        byte[] packet = Tcp(options, v6: v6);
        Assert.True(NetworkExperimentPackets.TryTcp(packet, out var parsed));
        Assert.Equal(7, parsed!.Options.WindowScale); Assert.True(parsed.Options.SackPermitted);
        Assert.Equal(42u, parsed.Options.TSval); Assert.Equal(19u, parsed.Options.TSecr); Assert.Equal(16384, parsed.Window);
        Assert.True(NetworkExperimentPackets.TryTcp(Tcp(Array.Empty<byte>(), v6: v6), out var absent));
        Assert.Null(absent!.Options.TSval); Assert.Null(absent.Options.WindowScale); Assert.False(absent.Options.SackPermitted); Assert.Empty(absent.Options.SackBlocks);
        var blocks = new byte[] { 5,10,0,0,0,10,0,0,0,20,0,0 };
        Assert.True(NetworkExperimentPackets.TryTcp(Tcp(blocks, false, v6), out var sack)); Assert.Equal("10:20", Assert.Single(sack!.Options.SackBlocks));
    }
    [Fact]
    public void RejectsMalformedOptionsFragmentsTruncationAndNonTcp()
    {
        foreach (byte[] option in new byte[][] { new byte[] {3,3,15,0}, new byte[] {8,9,0,0}, new byte[] {2,0,0,0}, new byte[] {5,3,0,0}, new byte[] {3,9,1,0} })
            Assert.False(NetworkExperimentPackets.TryTcp(Tcp(option), out _));
        Assert.False(NetworkExperimentPackets.TryTcp(Tcp(new byte[] {4,2,0,0}, false), out _));
        byte[] packet = Tcp(new byte[] {3,3,7,0});
        for (int n = 0; n < packet.Length; n++) Assert.False(NetworkExperimentPackets.TryTcp(packet.AsSpan(0,n), out _));
        packet[6] = 0x20; Assert.False(NetworkExperimentPackets.TryTcp(packet, out _));
        Assert.False(NetworkExperimentPackets.TryTcp(NetworkTechniquesTests.Packet(), out _));
    }
    [Fact]
    public async Task NatWithoutTopologyNeverSendsOrClaimsEffectAndConsentIsMandatory()
    {
        var report = await NetworkExperiments.RunAsync(NetworkExperiment.UdpNatLifetime, null, default);
        Assert.Equal(ExperimentStatus.RequiresLab, report.Status);
        Assert.Contains("Sent", JsonSerializer.Serialize(report.Evidence));
        foreach (var lab in new[] { new NatLabEndpoint(IPAddress.Loopback, 39435, false, new string('a',32)), new(IPAddress.Any, 39435, true, new string('a',32)), new(IPAddress.Parse("224.0.0.1"), 39435, true, new string('a',32)), new(IPAddress.Loopback, 80, true, new string('a',32)), new(IPAddress.Loopback, 39435, true, "bad") })
            Assert.Throws<ArgumentException>(() => NetworkExperiments.ValidateNat(lab));
    }
    [Fact]
    public async Task RealLoopbackReflectorProvesProtocolButNeverExternalNat()
    {
        using var reflector = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                var request = await reflector.ReceiveAsync(ct.Token); var json = JsonSerializer.Deserialize<JsonElement>(request.Buffer);
                foreach (string phase in new[] { "baseline", "delayed" })
                {
                    if (phase == "delayed") await Task.Delay(json.GetProperty("idle").GetInt32() * 1000, ct.Token);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { nonce = json.GetProperty("nonce").GetString(), phase, address = request.RemoteEndPoint.Address.ToString(), port = request.RemoteEndPoint.Port });
                    await reflector.SendAsync(bytes, request.RemoteEndPoint, ct.Token);
                }
            }
        }, ct.Token);
        var report = await NetworkExperiments.RunAsync(NetworkExperiment.UdpNatLifetime, new(IPAddress.Loopback, ((IPEndPoint)reflector.Client.LocalEndPoint!).Port, true, new string('a',32)), ct.Token);
        await server;
        Assert.Equal(ExperimentStatus.RequiresLab, report.Status);
        var trials = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(report.Evidence)); Assert.Equal(3, trials.GetArrayLength());
        foreach (var trial in trials.EnumerateArray())
        {
            Assert.True(trial.GetProperty("DelayedInboundReceived").GetBoolean()); Assert.False(trial.GetProperty("TranslationObserved").GetBoolean()); Assert.Equal(1, trial.GetProperty("ClientPacketsSent").GetInt32());
            Assert.True(trial.GetProperty("MeasuredQuietSeconds").GetDouble() >= trial.GetProperty("IdleSeconds").GetInt32() - 0.1);
            Assert.Equal(trial.GetProperty("MeasuredQuietSeconds").GetDouble(), trial.GetProperty("SurvivalLowerBoundSeconds").GetDouble());
        }
    }
}
