using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Controls;
using TileSmith.Core.Settings;
using TileSmith.Services;
using TileSmith.Theme;

namespace TileSmith.Views.Settings;

/// <summary>A settings page: a big title and short subtitle, then sections of cards.</summary>
public abstract class SettingsPage : StackPanel
{
    protected SettingsPage(AppHost host, SettingsWindow window, string title, string subtitle)
    {
        Host = host;
        Window = window;
        var header = new StackPanel { Spacing = 4, Margin = new Thickness(4, 0, 4, 6) };
        header.Children.Add(Kit.Title(title));
        header.Children.Add(Kit.Body(subtitle, 13.5, Palette.Current.Text2));
        Children.Add(header);
    }

    protected AppHost Host { get; }
    protected SettingsWindow Window { get; }
    protected AppSettings S => Host.Settings;

    protected void Save() => Host.SaveSettings();

    /// <summary>Adds a section label and a card of rows.</summary>
    protected Border Section(string title, string? subtitle, params UIElement?[] rows)
    {
        Children.Add(Kit.Section(title, subtitle));
        var card = Kit.Card(rows);
        Children.Add(card);
        return card;
    }

    public virtual void OnShown() { }

    /// <summary>Unsubscribe from app events here.</summary>
    public virtual void OnHidden() { }
}
