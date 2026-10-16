using TileSmith.Core.Ai;
using TileSmith.Core.Remote;
using TileSmith.Core.Settings;
using Xunit;

namespace TileSmith.Core.Tests;

public class ServerScriptTests
{
    [Fact]
    public void Scripts_reach_bash_with_unix_line_endings()
    {
        // C# raw strings carry the source file's CRLF; bash would read "do\r" and "{\r" as syntax errors
        var script = ServerConnection.ToUnixLineEndings("for c in a b; do\r\n  echo $c\r\ndone\r\n");
        Assert.DoesNotContain('\r', script);
        Assert.Equal("for c in a b; do\n  echo $c\ndone\n", script);
    }

    [Fact]
    public void Detection_reads_hermes_in_docker_and_its_model_endpoint()
    {
        static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));
        var stdout = string.Join('\n',
            "os=Ubuntu 26.04 LTS",
            "hermes_path=/home/fredde/.local/bin/hermes",
            "hermes_cmd=docker exec -i hermes hermes",
            "hermes_version=Hermes Agent v0.16.0 (2026.6.5)",
            "hermes_help=" + B64("usage: hermes chat [-q QUERY] [-m MODEL] [-Q]"),
            "hermes_config=" + B64("  Model:        {'provider': 'custom', 'default': 'cursor:auto', 'base_url': 'http://172.30.0.1:8378/v1'}"),
            "hermes_api=401",
            "cli_claude=/home/fredde/.local/bin/claude");
        var found = EngineDetector.Parse(stdout);
        Assert.Equal("docker exec -i hermes hermes", found.HermesCommand);
        Assert.Equal("-Q", found.HermesQuietFlag);
        Assert.False(found.HermesQueryFile);
        Assert.Equal("cursor:auto", found.HermesModel);
        Assert.Equal("http://172.30.0.1:8378/v1", found.HermesBaseUrl);
        Assert.Equal("hermes", found.Recommended);

        var ai = new AiSettings();
        EngineDetector.Apply(found, ai);
        Assert.Equal("docker exec -i hermes hermes", ai.HermesCommand);
        Assert.Equal("http://172.30.0.1:8378/v1", ai.HermesBaseUrl);
    }

    [Theory]
    [InlineData("  Model:        claude-sonnet-4", "claude-sonnet-4", null)]
    [InlineData("Model: {\"default\": \"x/y\", \"base_url\": \"http://h:1/v1\"}", "x/y", "http://h:1/v1")]
    [InlineData("", null, null)]
    public void Hermes_model_line_variants(string line, string? model, string? baseUrl)
    {
        var (m, b) = EngineDetector.ParseHermesModel(line);
        Assert.Equal(model, m);
        Assert.Equal(baseUrl, b);
    }
}

public class ProviderTests
{
    [Fact]
    public void Old_settings_migrate_to_providers()
    {
        var settings = new AppSettings { Ai = { Engine = "openai", LocalModel = "opus", DetectedEngine = "openai" } };
        SettingsStore.Migrate(settings);
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
        Assert.Equal("compatible", settings.Ai.Engine);
        Assert.Equal("compatible", settings.Ai.DetectedEngine);
        Assert.Equal("opus", settings.Ai.ModelFor("claude"));
        Assert.Null(settings.Ai.LocalModel);
    }

    [Fact]
    public void Api_keys_are_only_stored_encrypted()
    {
        var settings = new AppSettings();
        settings.Ai.For("openai").ProtectedKey = Secrets.Protect("sk-very-secret");
        var json = System.Text.Json.JsonSerializer.Serialize(settings, JsonDefaults.Options);
        Assert.DoesNotContain("sk-very-secret", json);
        Assert.Contains("protectedKey", json);
    }

    [Fact]
    public void Every_provider_has_a_unique_id_and_api_details()
    {
        Assert.Equal(ProviderCatalog.All.Count, ProviderCatalog.All.Select(p => p.Id).Distinct().Count());
        foreach (var p in ProviderCatalog.All.Where(p => p.Kind is ProviderKind.Cloud or ProviderKind.Local))
        {
            Assert.False(string.IsNullOrWhiteSpace(p.BaseUrl), p.Id);
            Assert.True(Uri.IsWellFormedUriString(p.BaseUrl, UriKind.Absolute), p.Id);
        }
    }

    [Fact]
    public void Picking_a_model_remembers_it_per_provider()
    {
        var ai = new AiSettings();
        ai.SetModel("openai", "gpt-5.2");
        ai.SetModel("claude", "sonnet");
        ai.SetModel("compatible", "llama3");
        Assert.Equal("gpt-5.2", ai.ModelFor("openai"));
        Assert.Equal("sonnet", ai.ModelFor("claude"));
        Assert.Equal("llama3", ai.OpenAiModel);
        Assert.Equal("compatible/llama3", ai.RecentModels[0]);
    }

    [Fact]
    public void Cloud_providers_need_a_key()
    {
        var settings = new AppSettings();
        var openai = ProviderCatalog.Get("openai");
        Assert.Equal(ProviderReadiness.NeedsKey, ProviderCatalog.Status(openai, settings).Readiness);
        settings.Ai.For("openai").ProtectedKey = Secrets.Protect("sk-test");
        Assert.True(ProviderCatalog.Status(openai, settings).IsReady);
    }

    [Fact]
    public void Fallback_chain_starts_with_the_choice_and_skips_unready_providers()
    {
        var settings = new AppSettings { Ai = { Engine = "gemini" } };
        settings.Ai.For("gemini").ProtectedKey = Secrets.Protect("k");
        settings.Ai.For("groq").ProtectedKey = Secrets.Protect("k");
        var chain = AiEngineFactory.Chain(settings);
        Assert.Equal("gemini", chain[0]);
        Assert.Contains("groq", chain);
        Assert.DoesNotContain("openai", chain); // no key
        Assert.DoesNotContain("hermes", chain); // no server

        settings.Ai.AutoFallback = false;
        Assert.Equal(["gemini"], AiEngineFactory.Chain(settings));
    }

    [Fact]
    public async Task Routed_backend_moves_on_when_a_provider_is_unreachable()
    {
        var calls = new List<string>();
        var switched = "";
        var routed = new RoutedBackend(
        [
            ("openai", () => new Fake("openai", calls, new AiException("offline", canFallback: true))),
            ("groq", () => new Fake("groq", calls, null)),
        ]);
        routed.Switched += (from, to, _) => switched = $"{from}->{to}";
        var reply = await routed.CompleteAsync([AiMessage.User("hi")], TestContext.Current.CancellationToken);
        Assert.Equal("groq says hi", reply);
        Assert.Equal(["openai", "groq"], calls);
        Assert.Equal("openai->Groq", switched);
        Assert.Equal("groq", routed.ProviderId);
    }

    [Fact]
    public async Task Routed_backend_does_not_hide_real_errors()
    {
        var routed = new RoutedBackend(
        [
            ("openai", () => new Fake("openai", [], new AiException("bad format"))),
            ("groq", () => new Fake("groq", [], null)),
        ]);
        await Assert.ThrowsAsync<AiException>(() => routed.CompleteAsync([AiMessage.User("hi")], TestContext.Current.CancellationToken));
    }

    private sealed class Fake(string name, List<string> calls, Exception? error) : IAiBackend
    {
        public string DisplayName => name;

        public Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
        {
            calls.Add(name);
            return error is null ? Task.FromResult($"{name} says hi") : Task.FromException<string>(error);
        }
    }

    [Fact]
    public void Model_lists_parse_openai_and_openrouter_shapes()
    {
        var openai = OpenAiBackend.ParseModelList("""{"data":[{"id":"gpt-old","created":1},{"id":"gpt-new","created":5}]}""");
        Assert.Equal(["gpt-new", "gpt-old"], openai.Select(m => m.Id));

        var router = OpenAiBackend.ParseModelList("""{"data":[{"id":"anthropic/claude-x","name":"Anthropic: Claude X","context_length":1000000}]}""");
        Assert.Equal("Anthropic: Claude X", router[0].Name);
        Assert.Equal("1M context", router[0].Detail);

        var gemini = OpenAiBackend.ParseModelList("""{"data":[{"id":"models/gemini-pro"}]}""");
        Assert.Equal("gemini-pro", gemini[0].Id);
    }

    [Fact]
    public void Chat_content_can_be_a_string_or_parts()
    {
        Assert.Equal("hi", OpenAiBackend.ParseContent("""{"choices":[{"message":{"content":"hi"}}]}"""));
        Assert.Equal("ab", OpenAiBackend.ParseContent("""{"choices":[{"message":{"content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}}]}"""));
    }

    [Fact]
    public void Non_chat_models_are_hidden()
    {
        var filtered = ModelCatalog.Filter(ProviderCatalog.Get("openai"),
            [new("gpt-5.2", "gpt-5.2"), new("text-embedding-3-large", "e"), new("whisper-1", "w"), new("gpt-image-1", "i")]);
        Assert.Equal(["gpt-5.2"], filtered.Select(m => m.Id));
    }

    [Fact]
    public void Cursor_model_list_is_parsed()
    {
        var models = LocalCliBackend.ParseCursorModels("Available models\n\nauto - Auto (default)\ngpt-5.2 - GPT-5.2\ngrok-4.7-low-fast - Grok 4.7  Low Fast​​\n");
        Assert.Equal(["auto", "gpt-5.2", "grok-4.7-low-fast"], models.Select(m => m.Id));
        Assert.Equal("Default", models[0].Detail);
        Assert.Equal("Grok 4.7 Low Fast", models[2].Name);
    }

    [Theory]
    [InlineData("""{"loggedIn": true, "email": "me@x.com", "subscriptionType": "pro"}""", true, "Signed in · me@x.com · Claude Pro")]
    [InlineData("""{"loggedIn": false}""", false, "Not signed in")]
    public void Claude_account_status_is_readable(string json, bool signedIn, string summary)
    {
        var account = LocalCliBackend.ParseAccount(LocalCli.Claude, json);
        Assert.Equal(signedIn, account.SignedIn);
        Assert.Equal(summary, account.Summary);
    }

    [Fact]
    public void Cursor_account_status_is_readable()
    {
        var account = LocalCliBackend.ParseAccount(LocalCli.Cursor, "✓ Logged in as me@x.com\n");
        Assert.True(account.SignedIn);
        Assert.Equal("Signed in as me@x.com", account.Summary);
    }
}
