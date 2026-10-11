using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Controls;
using TileSmith.Interop;
using TileSmith.Services;
using TileSmith.Theme;

namespace TileSmith.Views.Settings;

public sealed class GeneralPage : SettingsPage
{
    private static readonly string[] Hotkeys = ["Win+Shift+Q", "Win+Alt+Q", "Ctrl+Alt+T", "Alt+Shift+T", "Ctrl+Shift+Space", "Win+Alt+T", "Ctrl+Alt+Space"];
    private readonly Border _hotkeyWarning;

    public GeneralPage(AppHost host, SettingsWindow window)
        : base(host, window, "General", "How TileSmith starts and how you open the panel.")
    {
        Section("Startup", null,
            Kit.Row("power_settings_new", "Start with Windows", "TileSmith waits quietly next to the clock until you need it.",
                Kit.Toggle(StartupManager.IsEnabled(), on =>
                {
                    S.LaunchAtStartup = on;
                    StartupManager.Apply(on);
                    Save();
                })),
            Kit.Row("keep", "Always show the icon next to the clock", "Otherwise Windows may tuck it away in the hidden icons (^) menu.",
                Kit.Toggle(S.KeepIconVisible, on =>
                {
                    S.KeepIconVisible = on;
                    TrayIconVisibility.SetPromoted(on);
                    Save();
                })));

        var options = Hotkeys.Union([S.Hotkey]).Select(k => (k, k)).ToList();
        var select = Kit.Select(options, S.Hotkey, key =>
        {
            S.Hotkey = key;
            S.HotkeyChosenByUser = true;
            Save();
            CheckHotkey();
        });
        _hotkeyWarning = Kit.Badge("Taken by another app — pick another", Palette.Current.Warning, "warning");
        var shortcut = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        shortcut.Children.Add(_hotkeyWarning);
        shortcut.Children.Add(select);
        Section("Opening the panel", null,
            Kit.Row("keyboard", "Keyboard shortcut", "Open or close the panel from anywhere.",
                Kit.Toggle(S.HotkeyEnabled, on =>
                {
                    S.HotkeyEnabled = on;
                    select.IsEnabledForInput = on;
                    select.Opacity = on ? 1 : 0.5;
                    Save();
                    CheckHotkey();
                })),
            Kit.Row("touch_app", "Shortcut", null, shortcut),
            Kit.Row("mouse", "Tray icon", "Click the icon to open the panel; right-click for the menu.", null));
        select.IsEnabledForInput = S.HotkeyEnabled;
        select.Opacity = S.HotkeyEnabled ? 1 : 0.5;
        CheckHotkey();

        Section("The AI's history", null,
            Kit.Row("history", "Recent requests", $"{S.PromptHistory.Count} saved — press ↑ in the panel's prompt to reuse one.",
                Kit.Button("Clear", "delete_sweep", ChipStyle.Outline, () =>
                {
                    S.PromptHistory.Clear();
                    Save();
                    Window.Go("general");
                })));
    }

    private void CheckHotkey() =>
        _hotkeyWarning.Visibility = S.HotkeyEnabled && !App.Current.HotkeyOk ? Visibility.Visible : Visibility.Collapsed;
}
