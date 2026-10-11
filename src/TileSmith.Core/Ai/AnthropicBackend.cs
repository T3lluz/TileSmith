using System.Diagnostics;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace TileSmith.Core.Ai;

/// <summary>Claude through the Anthropic API with the user's own API key (official Anthropic SDK).</summary>
public sealed class AnthropicBackend(string apiKey, string model, string? effort, TimeSpan timeout, string? baseUrl = null) : IAiBackend
{
    /// <summary>Plenty for one tool call (a widget JSON is a few KB); stays well under non-streaming timeouts.</summary>
    private const int MaxTokens = 16000;

    public string DisplayName => ProviderCatalog.ModelLabel(ProviderCatalog.Get("anthropic"), model);

    private AnthropicClient Client(TimeSpan requestTimeout) =>
        string.IsNullOrWhiteSpace(baseUrl) || baseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase)
            ? new AnthropicClient { ApiKey = apiKey, Timeout = requestTimeout, MaxRetries = 1 }
            : new AnthropicClient { ApiKey = apiKey, Timeout = requestTimeout, MaxRetries = 1, BaseUrl = baseUrl.TrimEnd('/') };

    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        // the API wants alternating user/assistant turns that start with the user
        var merged = new List<(bool Assistant, string Text)>();
        foreach (var m in messages.Where(m => m.Role != "system"))
        {
            var assistant = m.Role == "assistant";
            if (merged.Count == 0 && assistant) continue;
            if (merged.Count > 0 && merged[^1].Assistant == assistant) merged[^1] = (assistant, merged[^1].Text + "\n\n" + m.Content);
            else merged.Add((assistant, m.Content));
        }
        var turns = merged.Select(t => new MessageParam { Role = t.Assistant ? Role.Assistant : Role.User, Content = t.Text }).ToList();

        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            System = system,
            Messages = turns,
        };
        if (ParseEffort(effort) is { } level) parameters = parameters with { OutputConfig = new OutputConfig { Effort = level } };

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await Client(timeout).Messages.Create(parameters, ct);
            Log.Info($"{DisplayName} replied in {sw.Elapsed.TotalSeconds:0.0}s ({response.StopReason})");
            if (response.StopReason == "refusal")
                throw new AiException("Claude declined this request. Try rephrasing it, or pick another model.", canFallback: true);
            var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            if (string.IsNullOrWhiteSpace(text)) throw new AiException($"{DisplayName} returned an empty reply ({response.StopReason}).");
            return text;
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            var page = await Client(TimeSpan.FromSeconds(15)).Models.List(new Anthropic.Models.Models.ModelListParams { Limit = 100 }, ct);
            return page.Items
                .Select(m => new ModelInfo(m.ID, m.DisplayName, m.MaxInputTokens is { } tokens ? $"{tokens / 1000:0}K context" : null))
                .ToList();
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    private static Effort? ParseEffort(string? effort) => effort?.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "xhigh" => Effort.Xhigh,
        "max" => Effort.Max,
        _ => null,
    };

    private Exception? Translate(Exception ex) => ex switch
    {
        AiException => null,
        OperationCanceledException => null,
        AnthropicUnauthorizedException => new AiException("The Anthropic API rejected the key (401). Check it in Settings › AI.", ex, canFallback: true),
        AnthropicForbiddenException => new AiException("This API key can't use that model (403).", ex, canFallback: true),
        AnthropicNotFoundException => new AiException($"The Anthropic API doesn't know the model \"{model}\". Pick another one in Settings › AI.", ex, canFallback: true),
        AnthropicRateLimitException => new AiException("The Anthropic API is rate limiting this key. Try again in a minute.", ex, canFallback: true),
        AnthropicBadRequestException => new AiException("The Anthropic API refused the request: " + ex.Message, ex),
        AnthropicIOException => new AiException("Couldn't reach the Anthropic API. Check your internet connection.", ex, canFallback: true),
        AnthropicApiException => new AiException("The Anthropic API failed: " + ex.Message, ex, canFallback: true),
        _ => null,
    };
}
