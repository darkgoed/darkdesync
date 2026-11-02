using System.Runtime.InteropServices;
using System.Security.Principal;
using DarkDesync.Core;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace DarkDesync.Tests;

[Collection("WinDivert controlled driver")]
public sealed class RestrictedTokenDriverTests(ITestOutputHelper output)
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags,
        uint disableSidCount, IntPtr disableSids, uint deletePrivilegeCount, IntPtr deletePrivileges,
        uint restrictSidCount, IntPtr restrictSids, out SafeAccessTokenHandle restricted);

    [DriverTheory, InlineData(true), Trait("Category", "Driver")]
    public void RealNativeOpenUnderNonAdministratorRestrictedTokenIsDenied(bool isolated)
    {
        Assert.True(isolated);
        using var identity = WindowsIdentity.GetCurrent();
        Assert.True(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "Driver job must be elevated before testing its restricted token");
        // LUA_TOKEN: remove elevated administrator membership and privileges. This
        // affects only this thread inside RunImpersonated, never the host account.
        Assert.True(CreateRestrictedToken(identity.AccessToken, 4, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out var token),
            "CreateRestrictedToken failed: " + Marshal.GetLastWin32Error());
        using (token)
        {
            WindowsIdentity.RunImpersonated(token, () =>
            {
                using var limited = WindowsIdentity.GetCurrent();
                Assert.False(new WindowsPrincipal(limited).IsInRole(WindowsBuiltInRole.Administrator));
                var handle = WinDivertNative.WinDivertOpen("false", WinDivertNative.Layer.Network, 0, 0);
                int error = Marshal.GetLastWin32Error();
                if (handle != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertClose(handle);
                Assert.Equal(WinDivertNative.InvalidHandle, handle); Assert.Equal(5, error);
                output.WriteLine("Real native WinDivertOpen under a non-administrator LUA restricted token returned ERROR_ACCESS_DENIED (5); impersonation scope restored.");
            });
        }
        Assert.True(new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
    }
}
