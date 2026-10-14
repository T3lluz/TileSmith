using System.Security.Principal;

namespace TileSmith.Core.Native;

/// <summary>Whether TileSmith runs with administrator rights (Settings › General › Run as administrator).</summary>
public static class Elevation
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    });

    /// <summary>True when this process has an elevated token: admin scripts run without a UAC prompt.</summary>
    public static bool IsElevated => Elevated.Value;

    private static readonly Lazy<bool> Admin = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            // with UAC, an administrator's normal token still lists the Administrators group (as deny-only)
            using var identity = WindowsIdentity.GetCurrent();
            return identity.Claims.Any(c => c.Value == "S-1-5-32-544");
        }
        catch
        {
            return false;
        }
    });

    /// <summary>The signed-in user is an administrator, so TileSmith can run elevated under the same account.</summary>
    public static bool CanElevate => IsElevated || Admin.Value;
}
