using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Controls;
using TileSmith.Core;
using TileSmith.Core.Ai;
using TileSmith.Core.Settings;
using TileSmith.Services;
using TileSmith.Theme;

namespace TileSmith.Views.Settings;

/// <summary>
/// Which AI builds the widgets: the one in use (with a test button), every provider TileSmith can talk to with its
/// status, setup for the selected one (sign-in, API key, address) with its live model list, and how runs are routed.
/// </summary>
public sealed class AiPage : SettingsPage
{
    private readonly StackPanel _hero = new();
    private readonly WrapPanel _grid = new() { HorizontalSpacing = 10, VerticalSpacing = 10 };
    private readonly StackPanel _detail = new();
    private readonly CancellationTokenSource _cts = new();
    private string _selected;
    private double _gridWidth;
    private Grid? _testResult;

    public AiPage(AppHost host, SettingsWindow window)
        : base(host, window, "AI", "Choose the AI that builds your widgets: your Claude or Cursor plan, Hermes on your server, or any provider with an API key.")
    {
        _selected = AiEngineFactory.ResolveEngine(S);
        Children.Add(new Border { Height = 18 });
        Children.Add(_hero);
        Children.Add(Kit.Section("Providers", "Pick one to set it up. A green dot means it's ready to use."));
        var gridHost = new Grid();
        gridHost.Children.Add(_grid);
        gridHost.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _gridWidth) < 1) return;
            _gridWidth = e.NewSize.Width;
            BuildGrid();
        };
        Children.Add(gridHost);
        Children.Add(_detail);

        Section("Behaviour", null,
            Kit.Row("alt_route", "Fall back automatically", "If the chosen AI can't be reached (offline, signed out, rate limited), TileSmith tries the next ready one and tells you.",
                Kit.Toggle(S.Ai.AutoFallback, on => { S.Ai.AutoFallback = on; Save(); })),
            Kit.Row("shield", "What the agent may do", "It runs PowerShell, edits files and tests actions on this PC to build widgets. Admin rights always ask through Windows.",
                Kit.Segmented([("auto", "Work freely"), ("ask", "Ask first")], S.Ai.Permission, id => { S.Ai.Permission = id; Save(); }, 230)),
            Kit.Row("timer", "Reply timeout", "How long to wait for one answer before giving up.",
                Kit.Select([("60", "1 minute"), ("120", "2 minutes"), ("240", "4 minutes"), ("480", "8 minutes"), ("900", "15 minutes")],
                    S.Ai.TimeoutSeconds.ToString(), id => { S.Ai.TimeoutSeconds = int.Parse(id); Save(); }, 150)));

        BuildHero();
        BuildDetail();
    }

    public override void OnHidden() => _cts.Cancel();

    private ProviderInfo Active => AiEngineFactory.ResolveProvider(S);

    // ── The AI in use ───────────────────────────────────────────────────────────────

    private void BuildHero()
    {
        var p = Palette.Current;
        _hero.Children.Clear();
        var provider = Active;
        var model = S.Ai.ModelFor(provider.Id);
        var status = ProviderCatalog.Status(provider, S);

        var grid = new Grid { Padding = new Thickness(20, 18, 18, 18), ColumnSpacing = 16, RowSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new Avatar(provider.Monogram, Palette.Parse(provider.Color), 52));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        texts.Children.Add(Ui.Text("IN USE", 11, p.Text3, semibold: true));
        texts.Children.Add(Ui.Text(ProviderCatalog.ModelLabel(provider, model), 20, p.Text1, semibold: true));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(Ui.Text($"{provider.Name} · {provider.KindLabel}{(S.Ai.Engine == "auto" ? " · picked automatically" : "")}", 12.5, p.Text2));
        line.Children.Add(Kit.Badge(status.Text, status.IsReady ? p.Success : p.Warning, status.IsReady ? "check_circle" : "warning"));
        texts.Children.Add(line);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var test = Kit.Button("Test", "bolt", ChipStyle.Outline);
        test.Click += (_, _) => _ = TestAsync(test);
        var change = Kit.Button("Change model", "swap_horiz", ChipStyle.Filled);
        change.Click += (_, _) =>
        {
            Flyout? flyout = null;
            var picker = new ModelPicker(Host, (providerId, picked) =>
            {
                flyout?.Hide();
                if (providerId == "auto") S.Ai.Engine = "auto";
                else
                {
                    S.Ai.Engine = providerId;
                    S.Ai.SetModel(providerId, picked);
                    _selected = providerId;
                }
                Save();
                Refresh();
            }, () => flyout?.Hide());
            flyout = ModelPicker.Flyout(picker);
            flyout.ShowAt(change);
        };
        buttons.Children.Add(test);
        buttons.Children.Add(change);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        grid.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 560;
            Grid.SetRow(buttons, narrow ? 1 : 0);
            Grid.SetColumn(buttons, narrow ? 1 : 2);
        };

        _testResult = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(Kit.Card(grid));
        stack.Children.Add(_testResult);
        _hero.Children.Add(stack);
    }

    private async Task TestAsync(Chip button)
    {
        if (_testResult is null) return;
        button.IsEnabledForInput = false;
        button.Text = "Testing…";
        _testResult.Children.Clear();
        var scanner = new WorkingScanner(5, 2) { Color = Palette.Current.Accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 4, 0, 0) };
        _testResult.Children.Add(scanner);
        var sw = Stopwatch.StartNew();
        Grid banner;
        try
        {
            var backend = Host.CreateBackend();
            var reply = await backend.CompleteAsync(
                [AiMessage.System("You are a connectivity check."), AiMessage.User("Reply with exactly this JSON and nothing else: {\"ok\": true}")],
                _cts.Token);
            var ok = reply.Contains("ok", StringComparison.OrdinalIgnoreCase);
            banner = ok
                ? Kit.Banner(Kit.Tone.Success, $"{backend.DisplayName} answered in {sw.Elapsed.TotalSeconds:0.0} s", "You're all set.")
                : Kit.Banner(Kit.Tone.Warning, $"Unexpected reply after {sw.Elapsed.TotalSeconds:0.0} s", reply.Length > 200 ? reply[..200] + "…" : reply);
            if (AiEngineFactory.NeedsServer(Active.Id, S.Ai)) Host.Server.MarkConnected();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (AiLoginRequiredException ex)
        {
            banner = Kit.Banner(Kit.Tone.Error, "Sign-in needed", ex.Message, Kit.Button("Sign in", "login", ChipStyle.Filled, () => LocalCliBackend.StartLogin(ex.Cli)));
        }
        catch (Exception ex)
        {
            banner = Kit.Banner(Kit.Tone.Error, "The test failed", ex.Message);
        }
        finally
        {
            button.IsEnabledForInput = true;
            button.Text = "Test";
        }
        _testResult.Children.Clear();
        _testResult.Children.Add(banner);
        Motion.Enter(banner, 8);
    }

    // ── Provider gallery ────────────────────────────────────────────────────────────

    private void BuildGrid()
    {
        var p = Palette.Current;
        _grid.Children.Clear();
        if (_gridWidth <= 0) return;
        var columns = _gridWidth >= 760 ? 3 : _gridWidth >= 480 ? 2 : 1;
        var width = Math.Floor((_gridWidth - (columns - 1) * 10) / columns) - 1;
        var active = Active.Id;
        foreach (var provider in ProviderCatalog.All)
        {
            var status = ProviderCatalog.Status(provider, S);
            var selected = provider.Id == _selected;
            var card = new Pressable { Width = width, Height = 74, CornerRadius = new CornerRadius(18), Fill = selected ? p.AccentSoft : p.Card, AccessibleName = provider.Name };
            card.BorderBrush = p.Brush(selected ? p.Accent : p.Hairline);
            card.BorderThickness = new Thickness(selected ? 1.5 : 1);
            var row = new Grid { Padding = new Thickness(14, 0, 12, 0), ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Avatar(provider.Monogram, Palette.Parse(provider.Color), 38));
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            nameRow.Children.Add(Ui.Text(provider.Name, 13.5, p.Text1, semibold: true));
            if (provider.Id == active) nameRow.Children.Add(Kit.Badge("In use", p.Accent));
            texts.Children.Add(nameRow);
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var dot = new LiveDot(7);
            dot.Set(status.IsReady ? p.Success : status.Readiness == ProviderReadiness.NotInstalled ? p.Text3 : p.Warning, false);
            statusRow.Children.Add(dot);
            statusRow.Children.Add(Ui.Text(status.IsReady ? provider.KindLabel : status.Text, 12, p.Text2));
            texts.Children.Add(statusRow);
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            card.SetContent(row);
            ToolTipService.SetToolTip(card, provider.Tagline);
            var id = provider.Id;
            card.Click += (_, _) =>
            {
                _selected = id;
                BuildGrid();
                BuildDetail(animate: true);
            };
            _grid.Children.Add(card);
        }
    }

    private void Refresh()
    {
        BuildHero();
        BuildGrid();
        BuildDetail();
    }

    // ── Setup for the selected provider ─────────────────────────────────────────────

    private void BuildDetail(bool animate = false)
    {
        var p = Palette.Current;
        _detail.Children.Clear();
        var provider = ProviderCatalog.Get(_selected);
        var isActive = Active.Id == provider.Id && S.Ai.Engine != "auto";
        var rows = new List<UIElement?>();

        // header
        var header = new Grid { Padding = new Thickness(18, 16, 16, 16), ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Avatar(provider.Monogram, Palette.Parse(provider.Color), 44));
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        titles.Children.Add(Ui.Text(provider.Name, 16, p.Text1, semibold: true));
        titles.Children.Add(Kit.Body(provider.Tagline, 12.5, p.Text2));
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        UIElement use = isActive
            ? Kit.Badge("In use", p.Accent, "check")
            : Kit.Button("Use this AI", "check", ChipStyle.Filled, () =>
            {
                S.Ai.Engine = provider.Id;
                Save();
                Refresh();
            });
        Grid.SetColumn((FrameworkElement)use, 2);
        ((FrameworkElement)use).VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(use);
        rows.Add(header);

        switch (provider.Kind)
        {
            case ProviderKind.Subscription:
                rows.Add(AccountRow(provider));
                break;
            case ProviderKind.Cloud:
                rows.Add(KeyRow(provider));
                break;
            case ProviderKind.Local:
                rows.Add(Kit.Row("lan", "Address", $"Where {provider.Name} listens on this PC.",
                    Kit.TextField(provider.BaseUrl ?? "", S.Ai.For(provider.Id).BaseUrl ?? "", v => { S.Ai.For(provider.Id).BaseUrl = v.Length == 0 ? null : v; Save(); }, width: 280)));
                rows.Add(Kit.Row("download", $"Get {provider.Name}", "Free; runs models on your own GPU or CPU.", Kit.Link("Download", provider.KeyUrl!)));
                break;
        }

        switch (provider.Id)
        {
            case "hermes":
                rows.Add(HermesServerRow());
                rows.Add(Kit.Row("terminal", "Command", "How Hermes is started on the server. Detect fills this in (e.g. docker exec -i hermes hermes).",
                    Kit.TextField("hermes", S.Ai.HermesCommand, v => { S.Ai.HermesCommand = v.Length == 0 ? "hermes" : v; Save(); }, width: 300, mono: true)));
                rows.Add(Kit.Row("data_object", "Extra arguments", "Optional, e.g. --provider openrouter",
                    Kit.TextField("", S.Ai.HermesArgs ?? "", v => { S.Ai.HermesArgs = v.Length == 0 ? null : v; Save(); }, width: 300, mono: true)));
                break;
            case "compatible":
                rows.Add(Kit.Row("link", "API address", "Hermes API server: http://127.0.0.1:8642/v1 · Ollama: http://127.0.0.1:11434/v1 · vLLM, LiteLLM…",
                    Kit.TextField("http://127.0.0.1:8642/v1", S.Ai.OpenAiBaseUrl, v => { S.Ai.OpenAiBaseUrl = v; Save(); Refresh(); }, width: 300, mono: true)));
                rows.Add(Kit.Row("vpn_lock", "Connect through the server", "When the API only listens on the server itself (most setups). Uses your SSH connection.",
                    Kit.Toggle(S.Ai.OpenAiViaTunnel, on => { S.Ai.OpenAiViaTunnel = on; Save(); Refresh(); })));
                rows.Add(Kit.Row("key", "API key", "Optional. Stored encrypted for your Windows account.",
                    Kit.PasswordField("Optional", Secrets.Unprotect(S.Ai.ProtectedOpenAiKey) ?? "", v => { S.Ai.ProtectedOpenAiKey = Secrets.Protect(v); Save(); }, out _, width: 300)));
                break;
            case "command":
                rows.Add(Kit.Row("terminal", "Command", "The prompt is piped to stdin, or use {file} for a prompt file. e.g. claude -p · ollama run qwen3 · llm -m gpt-5",
                    Kit.TextField("claude -p", S.Ai.CustomCommand ?? "", v => { S.Ai.CustomCommand = v.Length == 0 ? null : v; Save(); BuildGrid(); }, width: 300, mono: true)));
                break;
        }

        if (provider.SupportsEffort)
        {
            var effort = S.Ai.For(provider.Id).Effort ?? "";
            rows.Add(Kit.Row("speed", "Effort", "How hard the model thinks. Higher is slower but better at tricky widgets.",
                Kit.Segmented([("", "Default"), ("low", "Low"), ("medium", "Medium"), ("high", "High"), ("max", "Max")], effort,
                    id => { S.Ai.For(provider.Id).Effort = id.Length == 0 ? null : id; Save(); }, 340)));
        }

        if (provider.Id != "command") rows.Add(new ModelList(this, provider));

        var card = Kit.Card([.. rows]);
        card.Margin = new Thickness(0, 14, 0, 0);
        _detail.Children.Add(card);
        if (animate) Motion.Enter(card, 12);
    }

    private Grid AccountRow(ProviderInfo provider)
    {
        var cli = provider.Id == "claude" ? LocalCli.Claude : LocalCli.Cursor;
        var product = cli == LocalCli.Claude ? "Claude Code" : "Cursor Agent CLI";
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var row = Kit.Row("account_circle", "Account", "Checking…", actions);
        var description = ((StackPanel)row.Children[1]).Children.OfType<TextBlock>().Skip(1).FirstOrDefault();
        async Task CheckAsync()
        {
            if (LocalCliBackend.Locate(cli) is null)
            {
                if (description is not null) description.Text = $"{product} isn't installed on this PC. Install it, then sign in with your plan.";
                actions.Children.Clear();
                actions.Children.Add(Kit.Link("Get it", cli == LocalCli.Claude ? "https://claude.com/product/claude-code" : "https://cursor.com/cli", "download"));
                return;
            }
            var account = await LocalCliBackend.GetAccountStatusAsync(cli, _cts.Token);
            if (description is not null) description.Text = account.SignedIn
                ? $"{account.Summary}. Your sign-in stays with {product}; TileSmith never sees it."
                : $"{account.Summary}. Sign in with your {(cli == LocalCli.Claude ? "Claude" : "Cursor")} plan.";
            actions.Children.Clear();
            actions.Children.Add(Kit.Button("Refresh", "refresh", ChipStyle.Ghost, () => _ = CheckAsync()));
            actions.Children.Add(Kit.Button(account.SignedIn ? "Switch account" : "Sign in", "login", account.SignedIn ? ChipStyle.Outline : ChipStyle.Filled,
                () => LocalCliBackend.StartLogin(cli)));
        }
        _ = CheckAsync();
        return row;
    }

    private Grid KeyRow(ProviderInfo provider)
    {
        var settings = S.Ai.For(provider.Id);
        var field = Kit.PasswordField(provider.KeyPrefix is null ? "Paste your API key" : $"{provider.KeyPrefix}…", settings.ApiKey ?? "", value =>
        {
            settings.ProtectedKey = Secrets.Protect(value.Trim());
            Save();
            BuildGrid();
        }, out var box, width: 280);
        box.LostFocus += (_, _) => { if (box.Password.Length > 0) BuildHero(); };
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stack.Children.Add(field);
        stack.Children.Add(Kit.Link("Get a key", provider.KeyUrl!, "open_in_new"));
        return Kit.Row("key", "API key", "Stored encrypted for your Windows account only. Requests go straight from this PC to " + new Uri(provider.BaseUrl!).Host + ".", stack, stackBelow: 760);
    }

    private Grid HermesServerRow()
    {
        var p = Palette.Current;
        var server = Host.Server;
        string text;
        if (!S.Server.IsConfigured) text = "Add your server first — Hermes runs there.";
        else if (S.Ai.DetectedEngine == "hermes" || S.Ai.HermesCommand.StartsWith("docker", StringComparison.Ordinal))
            text = $"Found on {server.DisplayName}" + (S.Ai.HermesDefaultModel is null ? "." : $" · its model: {S.Ai.HermesDefaultModel}");
        else text = $"Not found on {server.DisplayName} yet. Press Detect on the Server page.";
        return Kit.Row("dns", "Server", text, Kit.Button("Server settings", "arrow_forward", ChipStyle.Outline, () => Window.Go("server")));
    }

    /// <summary>The provider's models: live list (cached), search, refresh, and any id typed by hand.</summary>
    private sealed class ModelList : StackPanel
    {
        private readonly AiPage _page;
        private readonly ProviderInfo _provider;
        private readonly StackPanel _rows = new() { Spacing = 2 };
        private readonly TextBox _search;
        private readonly TextBlock _note;
        private bool _loading;

        public ModelList(AiPage page, ProviderInfo provider)
        {
            _page = page;
            _provider = provider;
            var p = Palette.Current;
            Padding = new Thickness(16, 14, 16, 14);
            Spacing = 10;

            var top = new Grid { ColumnSpacing = 10 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(11), Background = p.Brush(Palette.WithAlpha(p.Accent, 0.14)), Child = Glyph.Make("neurology", 19, p.Accent) };
            top.Children.Add(icon);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            titles.Children.Add(Ui.Text("Model", 14, p.Text1, semibold: true));
            _note = Kit.Body("", 12.5, p.Text2);
            titles.Children.Add(_note);
            Grid.SetColumn(titles, 1);
            top.Children.Add(titles);
            if (provider.Id != "claude")
            {
                var refresh = Kit.Button("Refresh", "refresh", ChipStyle.Ghost, () => _ = LoadAsync());
                Grid.SetColumn(refresh, 2);
                top.Children.Add(refresh);
            }
            Children.Add(top);

            _search = Kit.SearchBox("Search, or type any model id and press Enter");
            _search.Margin = new Thickness(50, 0, 0, 0);
            _search.TextChanged += (_, _) => Render();
            _search.KeyDown += (_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Enter || _search.Text.Trim().Length == 0) return;
                e.Handled = true;
                Choose(_search.Text.Trim());
            };
            Children.Add(_search);
            var scroll = new ScrollViewer { Content = _rows, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(46, 0, 0, 0) };
            Children.Add(scroll);
            Render();
            var fetched = ModelCatalog.LastFetched(provider);
            if (provider.Id != "claude" && ProviderCatalog.Status(provider, page.S).IsReady && (fetched is null || DateTimeOffset.Now - fetched > TimeSpan.FromHours(6))) _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            if (_loading) return;
            _loading = true;
            _note.Text = "Loading the latest models…";
            try
            {
                var models = await ModelCatalog.RefreshAsync(_provider, _page.S, _page.Host.Server.GetConnection, _page._cts.Token);
                _note.Text = $"{models.Count} available · updated just now";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _note.Text = "Couldn't load the list: " + ex.Message;
                _note.Foreground = Palette.Current.Brush(Palette.Current.Warning);
                _loading = false;
                return;
            }
            _loading = false;
            Render();
        }

        private void Choose(string? id)
        {
            _page.S.Ai.SetModel(_provider.Id, id);
            _page.Save();
            _page.BuildHero();
            Render();
        }

        private void Render()
        {
            var p = Palette.Current;
            _rows.Children.Clear();
            var current = _page.S.Ai.ModelFor(_provider.Id) ?? "";
            var query = _search.Text.Trim();
            var models = ModelCatalog.Known(_provider).ToList();
            if (_provider.Kind is ProviderKind.Cloud or ProviderKind.Local or ProviderKind.Custom && models.All(m => m.Id.Length > 0))
                models.Insert(0, ModelInfo.Default(_provider.DefaultModel is null ? "The first model the API lists" : $"{_provider.DefaultModel}"));
            if (current.Length > 0 && models.All(m => m.Id != current)) models.Insert(1, new ModelInfo(current, current, "Typed by you"));
            if (!_loading && string.IsNullOrEmpty(_note.Text))
            {
                var fetched = ModelCatalog.LastFetched(_provider);
                _note.Text = _provider.Id == "claude"
                    ? "Aliases always point at Anthropic's latest version."
                    : fetched is { } f ? $"{models.Count - 1} available · updated {f.LocalDateTime:g}" : "Press Refresh to load the list.";
            }
            var shown = 0;
            foreach (var m in models)
            {
                if (query.Length > 0 && !m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !m.Id.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (++shown > 80) break;
                var selected = m.Id == current;
                var row = new Pressable { CornerRadius = new CornerRadius(12), Fill = selected ? p.AccentSoft : Colors.Transparent, MinHeight = 40 };
                var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 7, 12, 7) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var radio = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(9),
                    BorderBrush = p.Brush(selected ? p.Accent : p.HairlineStrong),
                    BorderThickness = new Thickness(selected ? 5 : 1.5),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                grid.Children.Add(radio);
                var name = Ui.TipWhenTrimmed(Ui.Text(m.Name, 13, p.Text1, semibold: selected));
                name.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);
                var detail = Ui.Text(m.Id.Length > 0 && m.Id != m.Name ? $"{m.Detail}{(m.Detail is null ? "" : " · ")}{m.Id}" : m.Detail ?? "", 11.5, p.Text3);
                detail.VerticalAlignment = VerticalAlignment.Center;
                detail.MaxWidth = 280;
                Grid.SetColumn(detail, 2);
                grid.Children.Add(detail);
                row.SetContent(grid);
                var id = m.Id;
                row.Click += (_, _) => Choose(id.Length == 0 ? null : id);
                _rows.Children.Add(row);
            }
            if (shown == 0)
            {
                _rows.Children.Add(Kit.Body(query.Length > 0 ? $"No match — press Enter to use “{query}” as the model id." : "No models listed yet.", 12.5, p.Text3));
            }
        }
    }
}
