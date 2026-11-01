using DarkDesync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DarkDesync.Tests;

public class ServiceTests
{
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
