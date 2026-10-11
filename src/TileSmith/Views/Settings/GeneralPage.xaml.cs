using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Interop;
using TileSmith.Services;

namespace TileSmith.Views.Settings;

public sealed partial class GeneralPage : UserControl
{
    private static readonly string[] Hotkeys = ["Win+Shift+Q", "Win+Alt+Q", "Ctrl+Alt+T", "Alt+Shift+T", "Ctrl+Shift+Space", "Win+Alt+T", "Ctrl+Alt+Space"];
    private readonly AppHost _host;
    private readonly string _initialTheme;
    private bool _loading = true;

    public GeneralPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        var s = host.Settings;
        StartupToggle.IsOn = StartupManager.IsEnabled();
        IconToggle.IsOn = s.KeepIconVisible;
        HotkeyToggle.IsOn = s.HotkeyEnabled;
        foreach (var key in Hotkeys.Union([s.Hotkey])) HotkeyCombo.Items.Add(key);
        HotkeyCombo.SelectedItem = s.Hotkey;
        HotkeyCombo.IsEnabled = s.HotkeyEnabled;
        Select(StyleCombo, s.PanelStyle);
        foreach (var (id, name, _) in Theme.Palette.AccentChoices) AccentCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        Select(AccentCombo, s.Accent);
        Select(ColumnsCombo, s.PanelColumns.ToString());
        Select(ThemeCombo, s.Theme);
        _initialTheme = s.Theme;
        CheckHotkey();
        _loading = false;
    }

    private static void Select(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? combo.Items[0];

    private void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.LaunchAtStartup = StartupToggle.IsOn;
        StartupManager.Apply(StartupToggle.IsOn);
        _host.SaveSettings();
    }

    private void IconToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.KeepIconVisible = IconToggle.IsOn;
        TrayIconVisibility.SetPromoted(IconToggle.IsOn);
        _host.SaveSettings();
    }

    private void HotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.HotkeyEnabled = HotkeyToggle.IsOn;
        HotkeyCombo.IsEnabled = HotkeyToggle.IsOn;
        _host.SaveSettings();
        CheckHotkey();
    }

    private void HotkeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HotkeyCombo.SelectedItem is not string key) return;
        _host.Settings.Hotkey = key;
        _host.Settings.HotkeyChosenByUser = true;
        _host.SaveSettings();
        CheckHotkey();
    }

    private void CheckHotkey()
    {
        if (!_host.Settings.HotkeyEnabled)
        {
            HotkeyWarning.Visibility = Visibility.Collapsed;
            return;
        }
        HotkeyWarning.Visibility = App.Current.HotkeyOk ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || StyleCombo.SelectedItem is not ComboBoxItem item) return;
        _host.Settings.PanelStyle = (string)item.Tag;
        _host.SaveSettings();
    }

    private void AccentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AccentCombo.SelectedItem is not ComboBoxItem item) return;
        _host.Settings.Accent = (string)item.Tag;
        _host.SaveSettings();
    }

    private void ColumnsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ColumnsCombo.SelectedItem is not ComboBoxItem item) return;
        _host.Settings.PanelColumns = int.Parse((string)item.Tag);
        _host.SaveSettings();
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not ComboBoxItem item) return;
        _host.Settings.Theme = (string)item.Tag;
        _host.SaveSettings();
        RestartButton.Visibility = _host.Settings.Theme != _initialTheme ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e) => App.Current.Restart();

}
