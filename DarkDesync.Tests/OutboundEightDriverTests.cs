using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DarkDesync.Core;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

[Collection("WinDivert controlled driver")]
public sealed class OutboundEightDriverTests(ITestOutputHelper output)
{
    [GlobalDriverTheory, Trait("Category", "Driver")]
    [MemberData(nameof(OutboundEightEffectsTests.Modes), MemberType = typeof(OutboundEightEffectsTests))]
    public async Task NativePacketsToOwnedSink_ExactPayloadScheduleLossAndStop(DiagnosticMode mode)
    {
        string dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../DarkDesync.UiTests/bin/Release/net8.0-windows/DarkDesync.UiTests.dll"));
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(dll); info.ArgumentList.Add("--udp-sink");
        using var child = Process.Start(info)!;
        using var engine = DiagnosticModesTests.Engine();
        try
        {
            string? portLine = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(int.TryParse(portLine, out int port) && port > 0, "Owned child sink must bind loopback");
            uint pid = (uint)Environment.ProcessId;
            engine.ObserveTarget(pid);
            using var sender = new UdpClient(); sender.Connect(IPAddress.Loopback, port);
            int sourcePort = ((IPEndPoint)sender.Client.LocalEndPoint!).Port;
            engine.OutboundSeedOverride = 42;
            // Explicit test-only isolation, not a production filter change. WPF
            // tests separately assert literal GLOBAL outbound for all 12 effects.
            // Background VM oversize packets must not cancel this measurement.
            engine.StartArmed(FilterMode.Custom, pid, 0, $"outbound and udp and udp.SrcPort == {sourcePort}");
            byte[] Payload(byte id) { var b = Enumerable.Range(0, 512).Select(i => (byte)(i ^ id)).ToArray(); b[0] = id; return b; }
            await sender.SendAsync(Payload(240), 512);
            Assert.Equal(Convert.ToBase64String(Payload(240)), await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await UdpDriverIntegrationTests.WaitUntil(() => engine.IsRunning && engine.ScopeStatus.UdpScoped > 0);
            Assert.Equal(0, engine.PacketsThrottled); Assert.Equal(0, engine.IntentionalBurstDrops);
            int count = mode == DiagnosticMode.GilbertElliottBurstLoss ? 100 : 12;
            var received = new List<byte>(); var times = new List<double>();
            var clock = Stopwatch.StartNew();
            var read = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await child.StandardOutput.ReadLineAsync(); if (line == null) break;
                    var bytes = Convert.FromBase64String(line); byte id = bytes[0];
                    Assert.Equal(Payload(id), bytes); // Exact bytes, not only counters.
                    if (id == 250) break;
                    received.Add(id); times.Add(clock.Elapsed.TotalMilliseconds);
                }
            });
            Assert.True(engine.TriggerArmedEffect(mode, 800)); clock.Restart();
            for (byte id = 0; id < count; id++) await sender.SendAsync(Payload(id), 512);
            await UdpDriverIntegrationTests.WaitUntil(() => engine.StateMachine.Current == DesyncStateMachine.State.Armed);
            Assert.True(engine.IsRunning); engine.Stop();
            await sender.SendAsync(Payload(250), 512); await read.WaitAsync(TimeSpan.FromSeconds(5));
            output.WriteLine($"Native measurement {mode}: source-port-isolated own UDP -> child; received=[{string.Join(',', received)}]; ms=[{string.Join(',', times.Select(t => t.ToString("F1")))}]; held={engine.PacketsThrottled}; reordered={engine.IntentionallyReorderedPairs}; drops={engine.IntentionalBurstDrops}; fallback={engine.LastShapingFallback ?? "none"}");
            Assert.Null(engine.LastShapingFallback);
            Assert.Equal(received.Count, received.Distinct().Count());
            if (mode == DiagnosticMode.GilbertElliottBurstLoss)
            {
                Assert.InRange(received.Count, 30, 99); Assert.True(engine.IntentionalBurstDrops > 0);
                Assert.True(engine.IntentionalBurstDrops >= count - received.Count);
                Assert.Equal(received.Order(), received);
            }
            else
            {
                Assert.Equal(Enumerable.Range(0, count).Select(i => (byte)i), received.Order());
                Assert.Equal(0, engine.IntentionalBurstDrops); Assert.True(engine.PacketsThrottled >= count);
                if (mode == DiagnosticMode.GapBasedReordering)
                {
                    Assert.True(engine.IntentionallyReorderedPairs > 0); Assert.False(received.SequenceEqual(received.Order()));
                    for (int i = 0; i < count; i++) Assert.InRange(Math.Abs(received[i] - i), 0, 1);
                }
                else Assert.Equal(received.Order(), received);
                if (mode == DiagnosticMode.TwoPhaseDrain) Assert.True(times[0] >= 200 && times[^1] - times[0] > 30);
            }
            Assert.False(engine.IsCaptureRequested); Assert.False(engine.IsRunning); Assert.Equal(0, engine.PacketsFailedToSend);
            output.WriteLine($"{mode}: real WinDivert -> own child UDP sink; ids=[{string.Join(',', received)}]; receive ms=[{string.Join(',', times.Select(t => t.ToString("F1")))}]; intentional drops={engine.IntentionalBurstDrops}; held={engine.PacketsThrottled}; reordered={engine.IntentionallyReorderedPairs}; exact payload and post-STOP sentinel verified. User PC/hardware keyboard not tested.");
        }
        finally { engine.Stop(); if (!child.HasExited) { child.Kill(true); child.WaitForExit(3000); } }
    }
}
