using System.Runtime.InteropServices;
using TileSmith.Core.Actions;

namespace TileSmith.Core.Native;

/// <summary>Sends keyboard shortcuts and text with SendInput.</summary>
public static class KeyboardInput
{
    private static readonly Dictionary<string, ushort> Keys = BuildKeyMap();

    private static readonly HashSet<ushort> Extended =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x90, 0x6F,
        0xA3, 0xA5, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,
    ];

    private static readonly HashSet<ushort> Modifiers = [0x10, 0x11, 0x12, 0x5B, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5];

    /// <summary>Parses "Win+Shift+S" / "Ctrl + Alt + T" and presses it.</summary>
    public static void SendShortcut(string shortcut)
    {
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (shortcut.EndsWith("++", StringComparison.Ordinal)) parts = [.. parts, "Plus"];
        if (parts.Length == 0) throw new ActionException("keys.send: no keys given.");

        var codes = new List<ushort>();
        foreach (var p in parts)
        {
            if (!TryMap(p, out var vk)) throw new ActionException($"keys.send: unknown key \"{p}\".");
            codes.Add(vk);
        }

        // modifiers first, then the rest; release in reverse order
        var ordered = codes.Where(Modifiers.Contains).Concat(codes.Where(c => !Modifiers.Contains(c))).ToList();
        var inputs = new List<INPUT>();
        foreach (var vk in ordered) inputs.Add(Key(vk, up: false));
        for (var i = ordered.Count - 1; i >= 0; i--) inputs.Add(Key(ordered[i], up: true));
        Send(inputs);
    }

    public static void SendKey(ushort vk) => Send([Key(vk, false), Key(vk, true)]);

    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n')
            {
                inputs.Add(Key(0x0D, false));
                inputs.Add(Key(0x0D, true));
                continue;
            }
            inputs.Add(Unicode(ch, false));
            inputs.Add(Unicode(ch, true));
        }
        Send(inputs);
    }

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var sent = SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<INPUT>());
        if (sent != inputs.Count) throw new ActionException("Windows blocked the simulated key press (is an elevated window focused?).");
    }

    private static bool TryMap(string name, out ushort vk)
    {
        var key = name.Trim();
        if (Keys.TryGetValue(key, out vk)) return true;
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; return true; }
        }
        if ((key.StartsWith('F') || key.StartsWith('f')) && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24)
        {
            vk = (ushort)(0x6F + f);
            return true;
        }
        return false;
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    private static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    private static Dictionary<string, ushort> BuildKeyMap()
    {
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["win"] = 0x5B, ["windows"] = 0x5B, ["lwin"] = 0x5B, ["meta"] = 0x5B, ["super"] = 0x5B, ["cmd"] = 0x5B,
            ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["menu"] = 0x5D, ["apps"] = 0x5D,
            ["tab"] = 0x09, ["enter"] = 0x0D, ["return"] = 0x0D, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20,
            ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
            ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
            ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["prtscn"] = 0x2C, ["print"] = 0x2C,
            ["capslock"] = 0x14, ["numlock"] = 0x90, ["scrolllock"] = 0x91, ["pause"] = 0x13,
            ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["volumemute"] = 0xAD,
            ["medianext"] = 0xB0, ["mediaprevious"] = 0xB1, ["mediaprev"] = 0xB1, ["mediastop"] = 0xB2, ["mediaplaypause"] = 0xB3, ["playpause"] = 0xB3,
            ["plus"] = 0xBB, ["equals"] = 0xBB, ["="] = 0xBB, ["minus"] = 0xBD, ["-"] = 0xBD, [","] = 0xBC, ["comma"] = 0xBC,
            ["."] = 0xBE, ["period"] = 0xBE, [";"] = 0xBA, ["semicolon"] = 0xBA, ["/"] = 0xBF, ["slash"] = 0xBF,
            ["`"] = 0xC0, ["backtick"] = 0xC0, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, ["backslash"] = 0xDC, ["'"] = 0xDE, ["quote"] = 0xDE,
        };
        return map;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1;
    private const uint KEYEVENTF_KEYUP = 0x2;
    private const uint KEYEVENTF_UNICODE = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
