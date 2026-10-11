using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using TileSmith.Core.Native;

namespace TileSmith.Core;

/// <summary>Facts about this PC that let the AI get things right the first time (supported resolutions etc.).</summary>
public static class DeviceContext
{
    public static string Collect()
    {
        var sb = new StringBuilder();
        Section(sb, () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var build = Environment.OSVersion.Version.Build;
            if (build >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            var display = key?.GetValue("DisplayVersion") as string;
            return $"- OS: {product} {display} (build {build}), {RuntimeInformation.OSArchitecture}, user \"{Environment.UserName}\", PC \"{Environment.MachineName}\"";
        });
        Section(sb, () => $"- AppData: {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}; user profile: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}");
        Section(sb, () =>
        {
            var displays = DisplayManager.GetDisplays();
            var lines = new StringBuilder("- Displays:");
            foreach (var d in displays)
            {
                lines.AppendLine();
                var tags = new List<string>();
                if (d.IsInternal) tags.Add("internal laptop panel");
                if (d.IsPrimary) tags.Add("primary");
                lines.Append($"  {d.Number}. \"{d.FriendlyName}\" ({d.DeviceName}{(tags.Count > 0 ? ", " + string.Join(", ", tags) : "")}) — current {d.Current.Width}x{d.Current.Height} @ {d.Current.RefreshRate} Hz");
                lines.AppendLine();
                var modes = d.ModesByResolution()
                    .Select(g => $"{g.Key} ({string.Join("/", g.Select(m => m.RefreshRate).Distinct().OrderDescending())} Hz)")
                    .Take(24);
                lines.Append($"     supported modes: {string.Join(", ", modes)}");
            }
            return lines.ToString();
        });
        Section(sb, () => $"- Brightness control: {(BrightnessControl.IsSupported() ? $"available (now {BrightnessControl.Get()}%)" : "not available")}");
        Section(sb, () =>
        {
            var b = PowerControl.GetBattery();
            return b.HasBattery ? $"- Battery: {b.Percent}%, {b.Status}" : "- Battery: none (desktop)";
        });
        Section(sb, () => $"- Power mode: {PowerControl.GetMode()}; power plans: {string.Join(", ", PowerControl.GetPlans().Select(p => p.Name + (p.Active ? " (active)" : "")))}");
        Section(sb, () => $"- Theme: {(ThemeControl.IsDark() ? "dark" : "light")}; speaker volume {AudioEndpoint.GetVolume()}%{(AudioEndpoint.GetMute() ? " (muted)" : "")}");
        return sb.ToString().TrimEnd();
    }

    private static void Section(StringBuilder sb, Func<string> collect)
    {
        try
        {
            sb.AppendLine(collect());
        }
        catch (Exception ex)
        {
            Log.Warn($"Device context section failed: {ex.Message}");
        }
    }
}
