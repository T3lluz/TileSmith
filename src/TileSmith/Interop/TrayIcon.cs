using System.Runtime.InteropServices;
using TileSmith.Core;
using TileSmith.Core.Native;
using static TileSmith.Interop.Native;

namespace TileSmith.Interop;

public sealed record TrayMenuItem(string Text, Action? Invoke, bool IsDefault = false, bool Enabled = true)
{
    public static readonly TrayMenuItem Separator = new("-", null);
}

/// <summary>
/// The notification-area icon (Shell_NotifyIcon, version 4) plus a hidden window that receives its clicks,
/// the global hotkey and system broadcasts (theme / display changes, Explorer restarts).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int CallbackMessage = WM_APP + 1;
    private const int HotkeyId = 0x7153;
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly WndProc _wndProc; // keep the delegate alive for the lifetime of the window
    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private string _tooltip = "TileSmith";
    private bool _badge;
    private bool _added;

    public event Action<int, int>? LeftClick;
    public event Func<IReadOnlyList<TrayMenuItem>>? MenuRequested;
    public event Action? HotkeyPressed;
    public event Action? ThemeChanged;
    public event Action? DisplayChanged;

    public TrayIcon()
    {
        try
        {
            SetPreferredAppMode(1); // AllowDark: menus follow the system theme
            FlushMenuThemes();
        }
        catch { /* older Windows */ }

        _wndProc = WindowProc;
        var className = "TileSmithTrayWindow";
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);
        // A hidden top-level window (not message-only) so it also receives TaskbarCreated / setting broadcasts.
        _hwnd = CreateWindowEx(0, className, "TileSmith", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        UpdateIcon();
        Add();
    }

    public IntPtr Handle => _hwnd;

    public string Tooltip
    {
        get => _tooltip;
        set
        {
            _tooltip = value.Length > 120 ? value[..120] : value;
            Modify();
        }
    }

    public bool ShowBadge
    {
        get => _badge;
        set
        {
            if (_badge == value) return;
            _badge = value;
            UpdateIcon();
            Modify();
        }
    }

    public void RefreshTheme()
    {
        UpdateIcon();
        Modify();
    }

    public bool RegisterHotkey(string shortcut)
    {
        UnregisterHotKey(_hwnd, HotkeyId);
        if (!TryParseHotkey(shortcut, out var mods, out var vk)) return false;
        var ok = Native.RegisterHotKey(_hwnd, HotkeyId, mods | MOD_NOREPEAT, vk);
        if (!ok) Log.Warn($"Hotkey {shortcut} is already used by another app.");
        return ok;
    }

    public void UnregisterHotkey() => UnregisterHotKey(_hwnd, HotkeyId);

    public static bool TryParseHotkey(string shortcut, out int modifiers, out int vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var raw in shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "win": modifiers |= MOD_WIN; break;
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "space": vk = 0x20; break;
                case var k when k.Length == 1 && char.IsLetterOrDigit(k[0]): vk = char.ToUpperInvariant(k[0]); break;
                case var f when f.StartsWith('f') && int.TryParse(f[1..], out var n) && n is >= 1 and <= 24: vk = 0x6F + n; break;
                default: return false;
            }
        }
        return vk != 0 && modifiers != 0;
    }

    private void UpdateIcon()
    {
        var dark = ThemeControl.IsTaskbarDark();
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", $"tray-{(dark ? "dark" : "light")}{(_badge ? "-badge" : "")}.ico");
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : GetDpiForSystem();
        var size = GetSystemMetricsForDpi(SM_CXSMICON, dpi == 0 ? 96 : dpi);
        var icon = LoadImage(IntPtr.Zero, file, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        if (icon == IntPtr.Zero) return;
        var old = _icon;
        _icon = icon;
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = "",
        uVersion = NOTIFYICON_VERSION_4,
    };

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (_added) Shell_NotifyIcon(NIM_SETVERSION, ref data);
        else Log.Warn("Shell_NotifyIcon(NIM_ADD) failed — will retry when Explorer restarts.");
    }

    private void Modify()
    {
        if (!_added) return;
        var data = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == CallbackMessage)
            {
                var evt = LowWord(lParam);
                var x = LowWord(wParam);
                var y = HighWord(wParam);
                switch (evt)
                {
                    case NIN_SELECT or NIN_KEYSELECT:
                        LeftClick?.Invoke(x, y);
                        break;
                    case WM_CONTEXTMENU:
                        ShowMenu(x, y);
                        break;
                }
                return IntPtr.Zero;
            }
            if (msg == TaskbarCreated)
            {
                _added = false;
                UpdateIcon();
                Add();
                return IntPtr.Zero;
            }
            switch (msg)
            {
                case WM_HOTKEY when (int)wParam == HotkeyId:
                    HotkeyPressed?.Invoke();
                    return IntPtr.Zero;
                case WM_SETTINGCHANGE:
                    if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                    {
                        RefreshTheme();
                        ThemeChanged?.Invoke();
                    }
                    break;
                case WM_DISPLAYCHANGE:
                    DisplayChanged?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Tray window message failed", ex);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var items = MenuRequested?.Invoke() ?? [];
        if (items.Count == 0) return;
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == TrayMenuItem.Separator || item.Text == "-") AppendMenu(menu, MF_SEPARATOR, 0, null);
                else AppendMenu(menu, MF_STRING | (item.Enabled ? 0 : MF_GRAYED), (nuint)(i + 1), item.Text);
                if (item.IsDefault) SetMenuDefaultItem(menu, (uint)(i + 1), 0);
            }
            SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
            var cmd = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_RIGHTALIGN, x, y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd > 0 && cmd <= items.Count) items[cmd - 1].Invoke?.Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        UnregisterHotKey(_hwnd, HotkeyId);
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        DestroyWindow(_hwnd);
    }
}
