using System.Net.Sockets;
using DarkDesync.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public class WinDivertDropIntegrationTests
{
    [Fact]
    public async Task PacketEngine_WithOutboundFilter_CapturesAndDropsPacketsWhenActive()
    {
        var stateMachine = new DesyncStateMachine();
        var scanner = new ProcessNetworkScanner(NullLogger<ProcessNetworkScanner>.Instance);
        var filterBuilder = new FilterBuilder(NullLogger<FilterBuilder>.Instance);
        var flowTracker = new FlowTracker(NullLogger<FlowTracker>.Instance);
        var engine = new PacketEngine(
            NullLogger<PacketEngine>.Instance,
            scanner,
            filterBuilder,
            flowTracker,
            stateMachine);

        // 1. Start engine with Outbound filter (Clumsy default)
        bool started = engine.Start(FilterMode.Outbound, processId: 0, dropPercentage: 100);
        if (!started)
        {
            // If WinDivert driver cannot be loaded in this specific test runner context, skip
            return;
        }

        try
        {
            await Task.Delay(100); // Give capture thread a moment to initialize

            // In IDLE state, send UDP packets -> should be intercepted and PASSED through
            using (var udpClient = new UdpClient())
            {
                byte[] testData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                for (int i = 0; i < 5; i++)
                {
                    udpClient.Send(testData, testData.Length, "8.8.8.8", 53);
                    await Task.Delay(20);
                }
            }

            await Task.Delay(200);
            long initialIntercepted = engine.PacketsIntercepted;
            long initialDropped = engine.PacketsDropped;
            long initialPassed = engine.PacketsPassedThrough;

            Assert.True(initialIntercepted > 0, $"Expected intercepted packets in IDLE, but got {initialIntercepted}");
            Assert.Equal(0, initialDropped); // No drops in IDLE state

            // 2. Activate state machine -> ACTIVE (Drop mode)
            bool actOk = stateMachine.TryActivate();
            Assert.True(actOk);
            Assert.Equal(DesyncStateMachine.State.Active, stateMachine.Current);

            // Send UDP packets during ACTIVE state -> MUST be DROPPED!
            using (var udpClient = new UdpClient())
            {
                byte[] testData = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 };
                for (int i = 0; i < 10; i++)
                {
                    udpClient.Send(testData, testData.Length, "1.1.1.1", 53);
                    await Task.Delay(20);
                }
            }

            await Task.Delay(300);
            long droppedCount = engine.PacketsDropped;

            // 3. Verify packet drop occurred
            Assert.True(droppedCount > 0, $"Expected dropped packets during ACTIVE state, but got {droppedCount}");

            // 4. Restore traffic -> RESTORING -> IDLE
            stateMachine.TryBeginRestore();
            await Task.Delay(50);
            stateMachine.TryComplete();
            Assert.Equal(DesyncStateMachine.State.Idle, stateMachine.Current);

            // Send packets in restored IDLE state -> should pass through again
            long droppedBefore = engine.PacketsDropped;
            using (var udpClient = new UdpClient())
            {
                byte[] testData = new byte[] { 1, 1, 1, 1 };
                for (int i = 0; i < 5; i++)
                {
                    udpClient.Send(testData, testData.Length, "8.8.8.8", 53);
                    await Task.Delay(20);
                }
            }

            await Task.Delay(200);
            // Confirm no further drops after restoration
            Assert.Equal(droppedBefore, engine.PacketsDropped);
        }
        finally
        {
            engine.Stop();
            engine.Dispose();
        }
    }
}
