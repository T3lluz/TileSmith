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
                Kit.Toggle(S.LaunchAtStartup, on =>
                {
                    S.LaunchAtStartup = on;
                    StartupManager.Apply(on, S.RunAsAdmin);
                    Save();
                })),
            Kit.Row("admin_panel_settings", "Run as administrator",
                (Core.Native.Elevation.IsElevated ? "On now. " : "") +
                "Widgets and the AI can change system settings without a Windows prompt each time; Windows asks once when TileSmith starts. " +
                "The AI's scripts then run as administrator too — set “What the agent may do” to “Ask first” to approve each one.",
                Core.Native.Elevation.CanElevate ? AdminToggle() : (UIElement)Kit.Badge("Needs an administrator account", Palette.Current.Text3, "lock")),
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

    private PillSwitch AdminToggle()
    {
        PillSwitch? toggle = null;
        toggle = Kit.Toggle(S.RunAsAdmin, on =>
        {
            S.RunAsAdmin = on;
            Save();
            if (on && !Core.Native.Elevation.IsElevated)
            {
                if (App.Current.RestartElevated()) return;
                // declined at the prompt: nothing changes
                S.RunAsAdmin = false;
                Save();
                toggle!.IsOn = false;
            }
            else if (!on && Core.Native.Elevation.IsElevated)
            {
                StartupManager.Apply(S.LaunchAtStartup, admin: false);
                App.Current.RestartAsUser();
            }
            else
            {
                StartupManager.Apply(S.LaunchAtStartup, on);
            }
        });
        return toggle;
    }

    private void CheckHotkey() =>
        _hotkeyWarning.Visibility = S.HotkeyEnabled && !App.Current.HotkeyOk ? Visibility.Visible : Visibility.Collapsed;
}
