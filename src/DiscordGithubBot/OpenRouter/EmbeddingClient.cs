using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.OpenRouter;

public interface IEmbeddingModel
{
    /// <summary>One vector per input, in input order.</summary>
    /// <exception cref="OpenRouterException">the call failed or the response does not match the inputs</exception>
    Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default);
}

/// <summary>
/// OpenRouter's embeddings endpoint (<c>POST /api/v1/embeddings</c>). Inputs go in batches, each retried once
/// on a transient failure; the cost of every batch — including a BYOK provider's upstream charge — feeds the
/// scope's <see cref="AiUsageMeter"/> like any other call.
/// </summary>
public sealed class EmbeddingClient(HttpClient http, AiUsageMeter usage, ILogger<EmbeddingClient> logger) : IEmbeddingModel
{
    public const string Path = "v1/embeddings";

    /// <summary>Inputs per request; well inside every listed provider's batch limit.</summary>
    internal const int BatchSize = 96;

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        string model, IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        var vectors = new List<float[]>(inputs.Count);
        foreach (var batch in inputs.Chunk(BatchSize))
        {
            try
            {
                vectors.AddRange(await AttemptAsync(model, batch, ct));
            }
            catch (OpenRouterException ex) when (ex.IsTransient && !ct.IsCancellationRequested)
            {
                logger.LogWarning("Embedding {Count} inputs with {Model} failed ({Reason}); retrying once.", batch.Length, model, ex.Message);
                vectors.AddRange(await AttemptAsync(model, batch, ct));
            }
        }

        return vectors;
    }

    private async Task<IReadOnlyList<float[]>> AttemptAsync(string model, string[] batch, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(Deadline);

        var request = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(batch.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
        };

        JsonObject body;
        try
        {
            using var response = await http.PostAsync(Path, JsonContent.Create(request), attempt.Token);
            if (!response.IsSuccessStatusCode) throw await OpenRouterChatClient.ErrorAsync(response, attempt.Token);
            body = await response.Content.ReadFromJsonAsync<JsonObject>(attempt.Token)
                ?? throw new OpenRouterException("OpenRouter returned an empty embedding response.", null, isTransient: true);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OpenRouterException($"no embeddings within {Deadline.TotalSeconds:0}s", null, isTransient: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OpenRouterException($"network failure: {ex.Message}", ex.StatusCode, isTransient: true, ex);
        }
        catch (JsonException ex)
        {
            throw new OpenRouterException("OpenRouter returned a body that is not an embedding response.", null, isTransient: true, ex);
        }

        var data = body["data"] as JsonArray ?? [];
        var vectors = new float[batch.Length][];
        foreach (var item in data)
        {
            var index = item?["index"]?.GetValue<int>() ?? -1;
            if (index < 0 || index >= batch.Length || item!["embedding"] is not JsonArray embedding) continue;
            vectors[index] = embedding.Select(v => v!.GetValue<float>()).ToArray();
        }

        if (vectors.Any(v => v is null))
            throw new OpenRouterException($"Embedding response for {model} is missing vectors.", null, isTransient: true);

        var cost = body["usage"]?["cost"]?.GetValue<decimal>() ?? 0;
        if (body["usage"]?["is_byok"]?.GetValue<bool>() == true)
            cost += body["usage"]?["cost_details"]?["upstream_inference_cost"]?.GetValue<decimal>() ?? 0;
        usage.Record(cost);

        return vectors;
    }
}
