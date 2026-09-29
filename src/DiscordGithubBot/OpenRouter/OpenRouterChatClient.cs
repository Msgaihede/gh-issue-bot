using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DiscordGithubBot.Configuration;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.OpenRouter;

/// <summary>Whether someone is waiting on the answer — it decides how long the first attempt may take.</summary>
public enum ChatUrgency
{
    /// <summary>A reporter is watching a deferred Discord interaction.</summary>
    Interactive,

    /// <summary>Nobody is waiting (repo-map upkeep); queueing on the flex tier costs nothing.</summary>
    Background,
}

/// <summary>Which OpenAI service tier a call is sent to first.</summary>
public enum ChatTier
{
    /// <summary>Half price, but it can queue: flex first, the regular tier behind it (<c>ChatProviders</c>).</summary>
    Flex,

    /// <summary>
    /// Full price, answers promptly (<c>RegularProviders</c>). For the calls a reporter waits on that matter
    /// most: measured, the draft took 3.2 s here against 7.2 s on flex.
    /// </summary>
    Regular,
}

/// <param name="Name">Names the response schema (letters, digits, <c>_</c>, <c>-</c>) and the call in logs.</param>
/// <param name="System">Instructions: the rules, the output contract.</param>
/// <param name="User">The material to work on — reports, issues, code — which is untrusted text.</param>
/// <param name="MaxTokens">Upper bound on reasoning plus output tokens, and so on the call's cost.</param>
/// <param name="Tier">The service tier tried first; a retry always goes to the regular tier.</param>
/// <param name="ReasoningEffort">
/// Overrides <c>OpenRouter:ReasoningEffort</c> for this prompt (one of its values); null uses the configured one.
/// </param>
public sealed record ChatPrompt(
    string Name, string System, string User, int MaxTokens = 8000, ChatTier Tier = ChatTier.Flex, string? ReasoningEffort = null);

public interface IOpenRouterChat
{
    /// <summary>
    /// One structured completion, parsed into <typeparamref name="T"/> (its schema is sent as strict JSON
    /// schema). Tried on the prompt's tier first, retried once on the regular tier after a missed deadline or
    /// a transient failure.
    /// </summary>
    /// <exception cref="OpenRouterException">every failure, once the retry is spent or when it cannot help</exception>
    Task<T> CompleteAsync<T>(ChatPrompt prompt, ChatUrgency urgency, CancellationToken ct = default) where T : class;
}

/// <summary>
/// Chat completions over OpenRouter. The cost lever is <c>provider.order</c>: by default OpenAI's flex
/// endpoint (half price) is first and the regular endpoint second, and OpenRouter itself moves on when flex
/// answers 429 or 5xx. What OpenRouter cannot see is flex <em>queueing</em> — the reason decision 71 once
/// reverted flex — so an interactive call also carries a client-side deadline, past which it is retried on
/// the regular tier. Background calls get a long deadline instead and happily wait out the queue. A prompt
/// marked <see cref="ChatTier.Regular"/> skips flex altogether.
/// </summary>
public sealed class OpenRouterChatClient(
    HttpClient http, BotOptions options, AiUsageMeter usage, ILogger<OpenRouterChatClient> logger) : IOpenRouterChat
{
    public const string Path = "v1/chat/completions";

    /// <summary>The retry of an interactive call runs on the regular tier, which answers in seconds.</summary>
    private static readonly TimeSpan InteractiveRetryDeadline = TimeSpan.FromMinutes(2);

    /// <summary>Background calls may sit in the flex queue; this only guards against a hung connection.</summary>
    private static readonly TimeSpan BackgroundDeadline = TimeSpan.FromMinutes(10);

    public async Task<T> CompleteAsync<T>(ChatPrompt prompt, ChatUrgency urgency, CancellationToken ct = default)
        where T : class
    {
        var o = options.OpenRouter;
        var firstDeadline = urgency == ChatUrgency.Interactive
            ? TimeSpan.FromSeconds(o.ChatDeadlineSeconds)
            : BackgroundDeadline;

        var firstProviders = prompt.Tier == ChatTier.Regular ? o.EffectiveRegularProviders : o.EffectiveChatProviders;
        try
        {
            return await AttemptAsync<T>(prompt, firstProviders, firstDeadline, ct);
        }
        catch (OpenRouterException ex) when (ex.IsTransient && !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Chat call {Name} on [{Providers}] failed ({Reason}); retrying once on [{RetryProviders}].",
                prompt.Name, string.Join(", ", firstProviders), ex.Message,
                string.Join(", ", o.EffectiveRegularProviders));
        }

        var retryDeadline = urgency == ChatUrgency.Interactive ? InteractiveRetryDeadline : BackgroundDeadline;
        return await AttemptAsync<T>(prompt, o.EffectiveRegularProviders, retryDeadline, ct);
    }

    private async Task<T> AttemptAsync<T>(
        ChatPrompt prompt, IReadOnlyList<string> providers, TimeSpan deadline, CancellationToken ct) where T : class
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(deadline);
        var started = Stopwatch.GetTimestamp();

        CompletionDto completion;
        try
        {
            using var response = await http.PostAsync(
                Path, JsonContent.Create(BuildRequest<T>(prompt, providers)), attempt.Token);
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, attempt.Token);

            completion = await response.Content.ReadFromJsonAsync<CompletionDto>(attempt.Token)
                ?? throw new OpenRouterException("OpenRouter returned an empty completion.", null, isTransient: true);
        }
        // The caller's own cancellation propagates untouched; anything else that cancels is our deadline
        // (or HttpClient's), which is exactly the "flex is queueing" case the retry exists for.
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OpenRouterException($"no answer within {deadline.TotalSeconds:0}s", null, isTransient: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OpenRouterException($"network failure: {ex.Message}", ex.StatusCode, isTransient: true, ex);
        }
        catch (JsonException ex)
        {
            throw new OpenRouterException("OpenRouter returned a body that is not a completion.", null, isTransient: true, ex);
        }

        var spend = completion.Usage?.Spend;
        usage.Record(spend);
        logger.LogDebug(
            "Chat call {Name} answered by {Model} via {Provider} in {Seconds:0.0} s: {Prompt} in / {Completion} out, ${Cost}{Byok}.",
            prompt.Name, completion.Model, completion.Provider, Stopwatch.GetElapsedTime(started).TotalSeconds,
            completion.Usage?.PromptTokens, completion.Usage?.CompletionTokens, spend,
            completion.Usage?.IsByok == true ? " (BYOK: billed by the provider)" : "");

        return ParseAnswer<T>(prompt.Name, completion);
    }

    /// <summary>
    /// A completion that stopped for any reason other than finishing, refused, or does not fit the schema is
    /// a failure, not a partial answer: a truncated JSON object parses into a silently incomplete one.
    /// Not transient — another provider is no likelier to fix a response the prompt produced.
    /// </summary>
    private static T ParseAnswer<T>(string name, CompletionDto completion) where T : class
    {
        var choice = completion.Choices?.FirstOrDefault()
            ?? throw new OpenRouterException($"Chat call {name} returned no choices.", null, isTransient: true);

        if (choice.Message?.Refusal is { Length: > 0 } refusal)
            throw new OpenRouterException($"Chat call {name} was refused: {refusal}", null, isTransient: false);
        // "error" is the upstream provider failing mid-answer, not the prompt: another attempt can fix it.
        if (choice.FinishReason == "error")
            throw new OpenRouterException($"Chat call {name} failed upstream.", null, isTransient: true);
        if (choice.FinishReason is not ("stop" or null))
            throw new OpenRouterException(
                $"Chat call {name} stopped early ({choice.FinishReason}).", null, isTransient: false);

        var content = choice.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new OpenRouterException($"Chat call {name} returned no content.", null, isTransient: false);

        try
        {
            return StructuredOutput.Parse<T>(content)
                ?? throw new OpenRouterException($"Chat call {name} returned null.", null, isTransient: false);
        }
        catch (JsonException ex)
        {
            throw new OpenRouterException($"Chat call {name} returned JSON outside its schema.", null, isTransient: false, ex);
        }
    }

    private JsonObject BuildRequest<T>(ChatPrompt prompt, IReadOnlyList<string> providers)
    {
        var o = options.OpenRouter;
        var request = new JsonObject
        {
            ["model"] = o.ChatModel,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = prompt.System },
                new JsonObject { ["role"] = "user", ["content"] = prompt.User }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = prompt.Name,
                    ["strict"] = true,
                    ["schema"] = StructuredOutput.SchemaFor<T>(),
                },
            },
            ["max_tokens"] = prompt.MaxTokens,
        };

        var effort = prompt.ReasoningEffort ?? o.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(effort))
            request["reasoning"] = new JsonObject { ["effort"] = effort.Trim() };

        // require_parameters keeps a fallback from landing on a host that would ignore the JSON schema;
        // service-tier endpoints such as openai/flex are only ever used when named here explicitly.
        if (providers.Count > 0)
        {
            request["provider"] = new JsonObject
            {
                ["order"] = new JsonArray(providers.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["allow_fallbacks"] = true,
                ["require_parameters"] = true,
            };
        }

        return request;
    }

    internal static async Task<OpenRouterException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? message = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(ct);
            message = error?.Error?.Message;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            // An error body that is not OpenRouter's envelope still has a status worth reporting.
        }

        return new OpenRouterException(
            $"OpenRouter answered {(int)response.StatusCode}: {message ?? response.ReasonPhrase}",
            response.StatusCode, OpenRouterException.IsTransientStatus(response.StatusCode));
    }

    internal sealed class ErrorEnvelope
    {
        [JsonPropertyName("error")] public ErrorDto? Error { get; set; }
    }

    internal sealed class ErrorDto
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class CompletionDto
    {
        [JsonPropertyName("model")] public string? Model { get; set; }
        [JsonPropertyName("provider")] public string? Provider { get; set; }
        [JsonPropertyName("choices")] public List<ChoiceDto>? Choices { get; set; }
        [JsonPropertyName("usage")] public UsageDto? Usage { get; set; }
    }

    private sealed class ChoiceDto
    {
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
        [JsonPropertyName("message")] public MessageDto? Message { get; set; }
    }

    private sealed class MessageDto
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("refusal")] public string? Refusal { get; set; }
    }

    internal sealed class UsageDto
    {
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
        [JsonPropertyName("cost")] public decimal? Cost { get; set; }
        [JsonPropertyName("is_byok")] public bool? IsByok { get; set; }
        [JsonPropertyName("cost_details")] public CostDetailsDto? CostDetails { get; set; }

        /// <summary>
        /// What the call actually cost. With BYOK (the account's own OpenAI key behind OpenRouter), <c>cost</c>
        /// is only OpenRouter's fee — usually 0 — and the provider bills the inference itself, which OpenRouter
        /// reports as <c>cost_details.upstream_inference_cost</c>. Without BYOK that field is already inside
        /// <c>cost</c>, so it is only added when the call was BYOK.
        /// </summary>
        public decimal? Spend =>
            Cost is null && CostDetails?.UpstreamInferenceCost is null
                ? null
                : (Cost ?? 0) + (IsByok == true ? CostDetails?.UpstreamInferenceCost ?? 0 : 0);
    }

    internal sealed class CostDetailsDto
    {
        [JsonPropertyName("upstream_inference_cost")] public decimal? UpstreamInferenceCost { get; set; }
    }
}
