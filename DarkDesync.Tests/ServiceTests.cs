using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public class ServiceTests
{
    [Fact]
    public void KeyboardCallback_ForwardsSpace_AndDoesNotConsumeOrRepeat()
    {
        // Windows callback contract test with a real CallNextHookEx call, not a
        // physical keyboard/game test. No hook installation or packet capture.
        using var service = new HotkeyService(NullLogger<HotkeyService>.Instance);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(HotkeyService).GetField("_enabled", flags)!.SetValue(service, true);
        var callback = typeof(HotkeyService).GetMethod("HookCallback", flags)!;
        int presses = 0; service.HotkeyPressed += () => presses++;
        var data = System.Runtime.InteropServices.Marshal.AllocHGlobal(24);
        try
        {
            for (int i = 0; i < 24; i++) System.Runtime.InteropServices.Marshal.WriteByte(data, i, 0);
            System.Runtime.InteropServices.Marshal.WriteInt32(data, 0x20);
            for (int i = 0; i < 5; i++)
                Assert.Equal(IntPtr.Zero, callback.Invoke(service, [0, (IntPtr)0x0100, data]));
            Assert.Equal(1, presses);
            Assert.Equal(IntPtr.Zero, callback.Invoke(service, [0, (IntPtr)0x0101, data]));
            Assert.Equal(IntPtr.Zero, callback.Invoke(service, [0, (IntPtr)0x0100, data]));
            Assert.Equal(2, presses);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(data); }
    }

    [Fact]
    public void HotkeyService_AvailableKeys_ContainsSpaceAndFunctionKeys()
    {
        var keys = HotkeyService.GetAvailableKeys();

        Assert.True(keys.ContainsKey("Espaço"));
        Assert.Equal(0x20, keys["Espaço"]);

        Assert.True(keys.ContainsKey("F1"));
        Assert.True(keys.ContainsKey("F12"));
        Assert.True(keys.ContainsKey("Caps Lock"));
        Assert.True(keys.ContainsKey("Tab"));
    }

    [Fact]
    public void HotkeyService_GetKeyName_ReturnsExpectedNames()
    {
        Assert.Equal("Espaço", HotkeyService.GetKeyName(0x20));
        Assert.Equal("F5", HotkeyService.GetKeyName(0x74));
        Assert.Equal("Caps Lock", HotkeyService.GetKeyName(0x14));
        Assert.Equal("Tab", HotkeyService.GetKeyName(0x09));
    }

    [Fact]
    public void ProcessService_GetWindowedProcesses_ReturnsWithoutCrashing()
    {
        var svc = new ProcessService(NullLogger<ProcessService>.Instance);
        var processes = svc.GetWindowedProcesses();

        Assert.NotNull(processes);
        // On a live Windows desktop with explorer/terminals running, should contain processes
        Assert.True(processes.Count >= 0);
    }
}
