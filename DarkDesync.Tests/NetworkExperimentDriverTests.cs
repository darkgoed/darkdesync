using System.Text.Json;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

[Collection("WinDivert controlled driver")]
public sealed class NetworkExperimentDriverTests(ITestOutputHelper output)
{
    [GlobalDriverTheory, Trait("Category", "Driver")]
    [InlineData(NetworkExperiment.TcpReceiveWindow, ExperimentStatus.ObservationOnly)]
    [InlineData(NetworkExperiment.TcpSackTimestamp, ExperimentStatus.ObservationOnly)]
    [InlineData(NetworkExperiment.IPv6FlowLabel, ExperimentStatus.Operational)]
    [InlineData(NetworkExperiment.IcmpFeedback, ExperimentStatus.ObservationOnly)]
    [InlineData(NetworkExperiment.NicOffload, ExperimentStatus.ObservationOnly)]
    public async Task RealOwnedLabProducesMeasuredEvidence(NetworkExperiment id, ExperimentStatus expected)
    {
        var report = await NetworkExperiments.RunAsync(id, null, default);
        output.WriteLine(JsonSerializer.Serialize(report));
        Assert.Equal(expected, report.Status);
        Assert.NotNull(report.Evidence);
        if (id == NetworkExperiment.IPv6FlowLabel)
        {
            var evidence = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(report.Evidence));
            var sent = evidence.GetProperty("Phases").EnumerateArray().Select(x => x.GetProperty("SentHex").GetString()!).ToArray();
            var captured = evidence.GetProperty("CapturedHex").EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.Equal(sent.OrderBy(x => x), captured.OrderBy(x => x));
            Assert.All(sent, x => NetworkTechniquesTests.AssertChecksums(Convert.FromHexString(x)));
        }
        // Never consider an unavailable or missing handshake a successful driver proof.
        if (id is NetworkExperiment.TcpReceiveWindow or NetworkExperiment.TcpSackTimestamp)
        {
            var families = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(report.Evidence));
            Assert.Equal(2, families.GetArrayLength());
            foreach (var family in families.EnumerateArray())
            {
                Assert.True(family.GetProperty("PayloadExact").GetBoolean());
                Assert.NotEmpty(family.GetProperty("PacketsHex").EnumerateArray());
                var observations = family.GetProperty("Observations").EnumerateArray().ToArray();
                Assert.Contains(observations, x => x.GetProperty("Syn").GetBoolean() && !x.GetProperty("Ack").GetBoolean());
                Assert.Contains(observations, x => x.GetProperty("Syn").GetBoolean() && x.GetProperty("Ack").GetBoolean());
                // SACK/timestamp absence is genuine OS behavior, not fabricated negotiation.
                Assert.All(observations, x => Assert.True(x.GetProperty("Window").GetInt32() >= 0));
            }
        }
    }

    [GlobalDriverTheory, Trait("Category", "Driver")]
    [InlineData(NetworkExperiment.TcpReceiveWindow)] [InlineData(NetworkExperiment.IPv6FlowLabel)]
    public async Task StopIsCancellationNotSuccessfulEvidenceAndNextRunReleasesHandles(NetworkExperiment id)
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var stopped = await NetworkExperiments.RunAsync(id, null, cancel.Token);
        Assert.Equal(ExperimentStatus.Unavailable, stopped.Status); Assert.Contains("STOP", stopped.Summary);
        var next = await NetworkExperiments.RunAsync(id, null, default);
        output.WriteLine(JsonSerializer.Serialize(next));
        Assert.Equal(id == NetworkExperiment.IPv6FlowLabel ? ExperimentStatus.Operational : ExperimentStatus.ObservationOnly, next.Status);
    }

    [GlobalDriverTheory, Trait("Category", "Driver"), InlineData(NetworkExperiment.TcpReceiveWindow)]
    public async Task StopDuringActiveCaptureReleasesSniffAndSockets(NetworkExperiment id)
    {
        using var cancel = new CancellationTokenSource();
        var work = NetworkExperiments.RunAsync(id, null, cancel.Token);
        await Task.Delay(20); cancel.Cancel();
        var report = await work;
        Assert.Equal(ExperimentStatus.Unavailable, report.Status); Assert.Contains("STOP", report.Summary);
        var next = await NetworkExperiments.RunAsync(id, null, default);
        Assert.Equal(ExperimentStatus.ObservationOnly, next.Status);
        output.WriteLine(JsonSerializer.Serialize(report));
    }
}
