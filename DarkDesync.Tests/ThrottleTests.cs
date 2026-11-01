using System.Net.Sockets;
using DarkDesync.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public class ThrottleTests
{
    [Fact]
    public void ThrottleProperties_DefaultAndClampedCorrectly()
    {
        var sm = new DesyncStateMachine();
        var scanner = new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance);
        var fb = new FilterBuilder(NullLogger<FilterBuilder>.Instance);
        var ft = new FlowTracker(NullLogger<FlowTracker>.Instance);
        var engine = new PacketEngine(NullLogger<PacketEngine>.Instance, scanner, fb, ft, sm);

        // Clumsy defaults
        Assert.False(engine.ThrottleEnabled);
        Assert.Equal(1000, engine.ThrottleTimeframeMs);
        Assert.Equal(100, engine.ThrottleChance);
        Assert.Equal(0, engine.ThrottleReleaseDelayMs);

        // Setting values
        engine.ThrottleEnabled = true;
        Assert.True(engine.ThrottleEnabled);

        engine.ThrottleTimeframeMs = 500;
        Assert.Equal(500, engine.ThrottleTimeframeMs);

        engine.ThrottleChance = 75;
        Assert.Equal(75, engine.ThrottleChance);

        engine.ThrottleReleaseDelayMs = 15;
        Assert.Equal(15, engine.ThrottleReleaseDelayMs);
    }

    [Fact]
    public async Task PacketEngine_ThrottleHoldsAndReleasesPackets()
    {
        var sm = new DesyncStateMachine();
        var scanner = new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance);
        var fb = new FilterBuilder(NullLogger<FilterBuilder>.Instance);
        var ft = new FlowTracker(NullLogger<FlowTracker>.Instance);
        var engine = new PacketEngine(NullLogger<PacketEngine>.Instance, scanner, fb, ft, sm)
        {
            ThrottleEnabled = true,
            ThrottleTimeframeMs = 200,
            ThrottleChance = 100,
            ThrottleReleaseDelayMs = 0 // Instant burst (Clumsy)
        };

        // Set drop to 0 so packets are throttled rather than dropped
        bool started = engine.Start(FilterMode.Outbound, processId: 0, dropPercentage: 0);
        if (!started) return;

        try
        {
            await Task.Delay(100);

            // Activate desync cycle
            sm.TryActivate();

            // Send packets
            using (var udp = new UdpClient())
            {
                byte[] data = new byte[] { 1, 2, 3, 4 };
                for (int i = 0; i < 5; i++)
                {
                    udp.Send(data, data.Length, "8.8.8.8", 53);
                    await Task.Delay(10);
                }
            }

            await Task.Delay(100);

            // Packets should be held in throttle queue
            Assert.True(engine.PacketsThrottled > 0, $"Expected throttled packets, got {engine.PacketsThrottled}");

            // Wait for timeframe to expire (200ms) + release
            await Task.Delay(300);

            // Packets should have been released
            Assert.True(engine.PacketsPassedThrough > 0);
        }
        finally
        {
            sm.ForceIdle();
            engine.Stop();
            engine.Dispose();
        }
    }
}
