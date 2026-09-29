using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiscordGithubBot.Configuration;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.OpenRouter;

public interface IDecisionModel
{
    /// <summary>
    /// Asks every question over one shared <paramref name="state"/> in a single request; the model answers
    /// them independently and in parallel, so none can see another's answer.
    /// </summary>
    /// <param name="purpose">Names the call in logs.</param>
    /// <exception cref="OpenRouterException">the call failed, or the response is not a Decisions response</exception>
    Task<DecisionResult> DecideAsync(
        string purpose, JsonNode state, IReadOnlyDictionary<string, DecisionQuestion> questions,
        CancellationToken ct = default);
}

/// <summary>
/// OpenRouter's Decisions API (<c>POST /api/alpha/decisions</c>): a decision model reads state and
/// answers typed questions with probabilities, never text. Answers are logged at Debug with the model
/// build that gave them, which is what threshold tuning reads (see <c>--dry-run</c>).
/// </summary>
public sealed class DecisionClient(
    HttpClient http, BotOptions options, AiUsageMeter usage, ILogger<DecisionClient> logger) : IDecisionModel
{
    public const string Path = "alpha/decisions";

    /// <summary>Decision models answer in well under a second; this only bounds a stuck connection.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    public async Task<DecisionResult> DecideAsync(
        string purpose, JsonNode state, IReadOnlyDictionary<string, DecisionQuestion> questions,
        CancellationToken ct = default)
    {
        var request = BuildRequest(state, questions);

        try
        {
            return await AttemptAsync(purpose, request, ct);
        }
        catch (OpenRouterException ex) when (ex.IsTransient && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Decision call {Purpose} failed ({Reason}); retrying once.", purpose, ex.Message);
        }

        return await AttemptAsync(purpose, request, ct);
    }

    private JsonObject BuildRequest(JsonNode state, IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
        var json = new JsonObject();
        foreach (var (key, question) in questions) json[key] = question.ToJson();

        return new JsonObject
        {
            ["model"] = options.OpenRouter.DecisionModel,
            ["state"] = state.DeepClone(),
            ["questions"] = json,
        };
    }

    private async Task<DecisionResult> AttemptAsync(string purpose, JsonObject request, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(Deadline);

        JsonObject body;
        try
        {
            using var response = await http.PostAsync(Path, JsonContent.Create(request), attempt.Token);
            if (!response.IsSuccessStatusCode) throw await OpenRouterChatClient.ErrorAsync(response, attempt.Token);

            body = await response.Content.ReadFromJsonAsync<JsonObject>(attempt.Token)
                ?? throw new OpenRouterException("OpenRouter returned an empty decision.", null, isTransient: true);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OpenRouterException($"no decision within {Deadline.TotalSeconds:0}s", null, isTransient: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OpenRouterException($"network failure: {ex.Message}", ex.StatusCode, isTransient: true, ex);
        }
        catch (JsonException ex)
        {
            throw new OpenRouterException("OpenRouter returned a body that is not a decision.", null, isTransient: true, ex);
        }

        if (body["answers"] is not JsonObject answers)
            throw new OpenRouterException($"Decision call {purpose} returned no answers.", null, isTransient: false);

        var model = body["model"]?.GetValue<string>() ?? options.OpenRouter.DecisionModel;
        var cost = body["usage"]?["cost"]?.GetValue<decimal>();
        var inputTokens = body["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        usage.Record(cost, inputTokens);

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Decision {Purpose} by {Model} ({Tokens} input tokens, ${Cost}): {Answers}",
                purpose, model, inputTokens, cost, answers.ToJsonString());

        return new DecisionResult(model, DecisionResult.ParseAnswers(answers));
    }
}
