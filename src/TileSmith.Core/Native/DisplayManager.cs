using System.Runtime.InteropServices;
using TileSmith.Core.Actions;

namespace TileSmith.Core.Native;

public sealed record DisplayMode(int Width, int Height, int RefreshRate)
{
    public string Resolution => $"{Width}x{Height}";
    public override string ToString() => $"{Width}x{Height} @ {RefreshRate} Hz";
}

public sealed class DisplayInfo
{
    public required string DeviceName { get; init; }
    public required string FriendlyName { get; init; }
    public required int Number { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsInternal { get; init; }
    public required DisplayMode Current { get; init; }
    public required IReadOnlyList<DisplayMode> Modes { get; init; }

    public IEnumerable<IGrouping<string, DisplayMode>> ModesByResolution() =>
        Modes.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.Width).GroupBy(m => m.Resolution);
}

/// <summary>
/// Enumerates displays and changes modes with the documented Win32 APIs
/// (EnumDisplaySettingsEx / ChangeDisplaySettingsEx / QueryDisplayConfig).
/// </summary>
public static class DisplayManager
{
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var targets = QueryTargets();
        var list = new List<DisplayInfo>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() })
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var name = dd.DeviceName;
            var current = NewDevMode();
            if (!EnumDisplaySettingsEx(name, ENUM_CURRENT_SETTINGS, ref current, 0)) continue;

            var modes = new HashSet<DisplayMode>();
            var dm = NewDevMode();
            for (var m = 0; EnumDisplaySettingsEx(name, m, ref dm, 0); m++)
            {
                if (dm.dmBitsPerPel is 32 or 0 && dm.dmDisplayFrequency > 1)
                    modes.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
            }

            targets.TryGetValue(name, out var target);
            var isInternal = target.IsInternal;
            var friendly = !string.IsNullOrWhiteSpace(target.FriendlyName) ? target.FriendlyName!
                         : isInternal ? "Built-in display"
                         : dd.DeviceString;
            if (isInternal) friendly = "Built-in display";

            list.Add(new DisplayInfo
            {
                DeviceName = name,
                FriendlyName = friendly,
                Number = list.Count + 1,
                IsPrimary = (dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                IsInternal = isInternal,
                Current = new DisplayMode(current.dmPelsWidth, current.dmPelsHeight, current.dmDisplayFrequency),
                Modes = modes.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.RefreshRate).ToList(),
            });
        }
        return list;
    }

    /// <summary>Resolves "primary", "internal", "external", "2", "\\.\DISPLAY2" or part of a monitor name.</summary>
    public static DisplayInfo Resolve(string? selector)
    {
        var displays = GetDisplays();
        if (displays.Count == 0) throw new ActionException("No active displays were found.");
        var s = selector?.Trim() ?? "";
        var lower = s.ToLowerInvariant();
        DisplayInfo Primary() => displays.FirstOrDefault(d => d.IsPrimary) ?? displays[0];

        if (lower is "" or "primary" or "main" or "default")
            return Primary();
        if (lower is "internal" or "integrated" or "embedded" || lower.Contains("laptop") || lower.Contains("built"))
            return displays.FirstOrDefault(d => d.IsInternal) ?? Primary();
        if (lower is "external" or "monitor" or "secondary")
            return displays.FirstOrDefault(d => !d.IsInternal && (!d.IsPrimary || displays.Count == 1))
                ?? displays.FirstOrDefault(d => !d.IsInternal)
                ?? throw new ActionException("No external display is connected.");
        if (int.TryParse(s, out var n))
            return displays.FirstOrDefault(d => d.Number == n) ?? throw new ActionException($"Display {n} isn't connected (found {displays.Count}).");
        return displays.FirstOrDefault(d => d.DeviceName.Equals(s, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault(d => d.FriendlyName.Contains(s, StringComparison.OrdinalIgnoreCase))
            ?? throw new ActionException($"No display matches \"{selector}\".");
    }

    public static void SetResolution(string? selector, int width, int height, int? refreshRate)
    {
        var display = Resolve(selector);
        var candidates = display.Modes.Where(m => m.Width == width && m.Height == height).ToList();
        if (candidates.Count == 0)
        {
            var supported = string.Join(", ", display.ModesByResolution().Select(g => g.Key).Take(12));
            throw new ActionException($"{display.FriendlyName} doesn't support {width}×{height}. Supported: {supported}.");
        }

        int hz;
        if (refreshRate is > 0 && candidates.Any(m => m.RefreshRate == refreshRate)) hz = refreshRate.Value;
        else if (candidates.Any(m => m.RefreshRate == display.Current.RefreshRate)) hz = display.Current.RefreshRate;
        else hz = candidates.Max(m => m.RefreshRate);

        if (display.Current.Width == width && display.Current.Height == height && display.Current.RefreshRate == hz) return;
        Apply(display, width, height, hz);
    }

    public static void SetRefreshRate(string? selector, int hz)
    {
        var display = Resolve(selector);
        var c = display.Current;
        var available = display.Modes.Where(m => m.Width == c.Width && m.Height == c.Height).Select(m => m.RefreshRate).Distinct().OrderDescending().ToList();
        if (!available.Contains(hz))
            throw new ActionException($"{display.FriendlyName} can't do {hz} Hz at {c.Width}×{c.Height}. Available: {string.Join(", ", available)} Hz.");
        if (c.RefreshRate == hz) return;
        Apply(display, c.Width, c.Height, hz);
    }

    private static void Apply(DisplayInfo display, int width, int height, int hz)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettingsEx(display.DeviceName, ENUM_CURRENT_SETTINGS, ref dm, 0))
            throw new ActionException($"Couldn't read the current mode of {display.FriendlyName}.");
        dm.dmPelsWidth = width;
        dm.dmPelsHeight = height;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

        var test = ChangeDisplaySettingsEx(display.DeviceName, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != DISP_CHANGE_SUCCESSFUL) throw new ActionException(Describe(test, display, width, height, hz));

        var result = ChangeDisplaySettingsEx(display.DeviceName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (result == DISP_CHANGE_RESTART) throw new ActionException("Windows needs a restart to apply this display mode.");
        if (result != DISP_CHANGE_SUCCESSFUL) throw new ActionException(Describe(result, display, width, height, hz));
        Log.Info($"Display {display.DeviceName} set to {width}x{height}@{hz}");
    }

    /// <summary>Validates a mode with CDS_TEST without applying it.</summary>
    public static bool CanApply(DisplayInfo display, int width, int height, int hz)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettingsEx(display.DeviceName, ENUM_CURRENT_SETTINGS, ref dm, 0)) return false;
        dm.dmPelsWidth = width;
        dm.dmPelsHeight = height;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        return ChangeDisplaySettingsEx(display.DeviceName, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
    }

    private static string Describe(int code, DisplayInfo d, int w, int h, int hz) => code switch
    {
        DISP_CHANGE_BADMODE => $"{d.FriendlyName} rejected {w}×{h} @ {hz} Hz.",
        DISP_CHANGE_FAILED => $"The graphics driver failed to switch {d.FriendlyName} to {w}×{h}.",
        DISP_CHANGE_NOTUPDATED => "Couldn't save the display settings.",
        _ => $"Changing the display mode failed (code {code}).",
    };

    private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    private readonly record struct TargetInfo(string? FriendlyName, bool IsInternal);

    /// <summary>Maps GDI device names (\\.\DISPLAY1) to monitor names and connector type.</summary>
    private static Dictionary<string, TargetInfo> QueryTargets()
    {
        var result = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0) return result;
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;

            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;

                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                target.header.adapterId = path.targetInfo.adapterId;
                target.header.id = path.targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref target) != 0) continue;

                var tech = target.outputTechnology;
                var isInternal = tech is OUTPUT_INTERNAL or OUTPUT_LVDS or OUTPUT_DISPLAYPORT_EMBEDDED or OUTPUT_UDI_EMBEDDED;
                result.TryAdd(source.viewGdiDeviceName, new TargetInfo(target.monitorFriendlyDeviceName, isInternal));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"QueryDisplayConfig failed: {ex.Message}");
        }
        return result;
    }

    #region Win32

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const int DM_PELSWIDTH = 0x80000;
    private const int DM_PELSHEIGHT = 0x100000;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x01;
    private const uint CDS_TEST = 0x02;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISP_CHANGE_RESTART = 1;
    private const int DISP_CHANGE_FAILED = -1;
    private const int DISP_CHANGE_BADMODE = -2;
    private const int DISP_CHANGE_NOTUPDATED = -3;
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint OUTPUT_LVDS = 6;
    private const uint OUTPUT_DISPLAYPORT_EMBEDDED = 11;
    private const uint OUTPUT_UDI_EMBEDDED = 13;
    private const uint OUTPUT_INTERNAL = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    #endregion
}
