using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TileSmith.Core.Actions;
using Windows.Devices.Radios;

namespace TileSmith.Core.Native;

public static class ThemeControl
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    public static bool IsTaskbarDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("SystemUsesLightTheme") is not int v || v == 0;
    }

    public static void Set(bool dark, string scope)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
        var light = dark ? 0 : 1;
        if (scope is "both" or "apps") key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
        if (scope is "both" or "system") key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
        Win32.BroadcastSettingChange("ImmersiveColorSet");
    }
}

public static class PowerControl
{
    private static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid Balanced = Guid.Empty;
    private static readonly Guid BetterPerformance = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static string GetMode()
    {
        if (PowerGetEffectiveOverlayScheme(out var guid) != 0) return "balanced";
        if (guid == Efficiency) return "efficiency";
        if (guid == BestPerformance) return "performance";
        return "balanced";
    }

    public static void SetMode(string mode)
    {
        var guid = mode.ToLowerInvariant() switch
        {
            "efficiency" or "battery" or "saver" or "best power efficiency" => Efficiency,
            "performance" or "best performance" or "max" => BestPerformance,
            "better performance" => BetterPerformance,
            _ => Balanced,
        };
        var hr = PowerSetActiveOverlayScheme(guid);
        if (hr != 0)
            throw new ActionException("Power mode can only be changed while the Balanced power plan is active.");
    }

    public sealed record PowerPlan(Guid Guid, string Name, bool Active);

    public static IReadOnlyList<PowerPlan> GetPlans()
    {
        var output = Processes.Run("powercfg.exe", "/list", TimeSpan.FromSeconds(10)).Stdout;
        var plans = new List<PowerPlan>();
        foreach (Match m in Regex.Matches(output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\((.+?)\)(\s*\*)?"))
        {
            plans.Add(new PowerPlan(Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Success));
        }
        return plans;
    }

    public static string GetPlan() => GetPlans().FirstOrDefault(p => p.Active)?.Name ?? "Unknown";

    public static void SetPlan(string? name, string? guid)
    {
        var plans = GetPlans();
        var plan = guid is not null && Guid.TryParse(guid, out var g)
            ? plans.FirstOrDefault(p => p.Guid == g)
            : plans.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
              ?? plans.FirstOrDefault(p => name is not null && p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (plan is null)
            throw new ActionException($"Power plan \"{name ?? guid}\" not found. Available: {string.Join(", ", plans.Select(p => p.Name))}.");
        var result = Processes.Run("powercfg.exe", $"/setactive {plan.Guid}", TimeSpan.FromSeconds(10));
        if (result.ExitCode != 0) throw new ActionException($"powercfg failed: {result.Stderr.Trim()}");
    }

    public static void PerformAction(string action)
    {
        switch (action.ToLowerInvariant())
        {
            case "lock":
                if (!LockWorkStation()) throw new ActionException("Couldn't lock the PC.");
                break;
            case "sleep":
                SetSuspendState(false, false, false);
                break;
            case "hibernate":
                if (!SetSuspendState(true, false, false)) throw new ActionException("Hibernate is not enabled on this PC.");
                break;
            case "shutdown":
                Processes.Run("shutdown.exe", "/s /t 0", TimeSpan.FromSeconds(10));
                break;
            case "restart":
                Processes.Run("shutdown.exe", "/r /t 0", TimeSpan.FromSeconds(10));
                break;
            case "signout":
                Processes.Run("shutdown.exe", "/l", TimeSpan.FromSeconds(10));
                break;
            case "displayoff":
                Win32.TurnOffDisplay();
                break;
            default:
                throw new ActionException($"Unknown power action \"{action}\".");
        }
    }

    public static (int Percent, string Status, bool HasBattery) GetBattery()
    {
        if (!GetSystemPowerStatus(out var s)) throw new ActionException("Couldn't read the battery status.");
        var hasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
        var percent = s.BatteryLifePercent == 255 ? 100 : s.BatteryLifePercent;
        var status = !hasBattery ? "No battery"
                   : (s.BatteryFlag & 8) != 0 ? "Charging"
                   : s.ACLineStatus == 1 ? "Plugged in"
                   : "On battery";
        return (percent, status, hasBattery);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlaySchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

public static class BrightnessControl
{
    public static bool IsSupported()
    {
        try { _ = Get(); return true; }
        catch { return false; }
    }

    public static int Get()
    {
        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
        foreach (var obj in searcher.Get())
        {
            using (obj) return Convert.ToInt32(obj["CurrentBrightness"]);
        }
        throw new ActionException("This display doesn't support brightness control (external monitors usually don't).");
    }

    public static void Set(int level)
    {
        level = Math.Clamp(level, 0, 100);
        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
        var any = false;
        foreach (ManagementObject obj in searcher.Get())
        {
            using (obj)
            {
                obj.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)level]);
                any = true;
            }
        }
        if (!any) throw new ActionException("This display doesn't support brightness control (external monitors usually don't).");
    }
}

public static class RadioControl
{
    public static async Task<bool> GetAsync(string kind)
    {
        var radio = await FindAsync(kind);
        return radio.State == RadioState.On;
    }

    public static async Task SetAsync(string kind, string state)
    {
        var radio = await FindAsync(kind);
        var on = state.ToLowerInvariant() switch
        {
            "on" or "true" => true,
            "off" or "false" => false,
            _ => radio.State != RadioState.On,
        };
        var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        if (result != RadioAccessStatus.Allowed)
            throw new ActionException($"Windows didn't allow changing {radio.Name} ({result}). Check Settings › Privacy › Radios.");
    }

    private static async Task<Radio> FindAsync(string kind)
    {
        var target = kind.ToLowerInvariant() switch
        {
            "bluetooth" or "bt" => RadioKind.Bluetooth,
            _ => RadioKind.WiFi,
        };
        var access = await Radio.RequestAccessAsync();
        if (access != RadioAccessStatus.Allowed)
            throw new ActionException("Radio control isn't allowed. Turn on Settings › Privacy & security › Radios.");
        var radios = await Radio.GetRadiosAsync();
        return radios.FirstOrDefault(r => r.Kind == target)
               ?? throw new ActionException(target == RadioKind.WiFi ? "No Wi-Fi adapter found." : "No Bluetooth adapter found.");
    }
}

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

public static class Processes
{
    public static ProcessResult Run(string file, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new ActionException($"Couldn't start {file}.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new ActionException($"{file} timed out.");
        }
        return new ProcessResult(p.ExitCode, stdout.Result, stderr.Result);
    }
}

internal static class Win32
{
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    public static void BroadcastSettingChange(string area)
    {
        var ptr = Marshal.StringToHGlobalUni(area);
        try { SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, ptr, SMTO_ABORTIFHUNG, 200, out _); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static void TurnOffDisplay() =>
        PostMessage((IntPtr)HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
