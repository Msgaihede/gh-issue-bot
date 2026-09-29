using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>
/// Answers calls in order from a script — the last step repeats — and records every request body. Unlike
/// <see cref="FakeHttpMessageHandler"/> a step may throw or hang, which is what retry and deadline tests need.
/// </summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly List<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _steps = new();
    private int _calls;

    public List<(string Url, JsonObject? Body)> Requests { get; } = new();

    public ScriptedHttpHandler Then(HttpStatusCode status, string json)
    {
        _steps.Add((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));
        return this;
    }

    public ScriptedHttpHandler ThenThrow(Exception ex)
    {
        _steps.Add((_, _) => Task.FromException<HttpResponseMessage>(ex));
        return this;
    }

    /// <summary>Never answers: waits until the request's token is cancelled, like a request stuck in a queue.</summary>
    public ScriptedHttpHandler ThenHang()
    {
        _steps.Add(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.RequestUri!.ToString(), body is null ? null : JsonNode.Parse(body) as JsonObject));

        var step = _steps[Math.Min(Interlocked.Increment(ref _calls) - 1, _steps.Count - 1)];
        return await step(request, ct);
    }

    public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("https://openrouter.ai/api/") };

    private sealed class UnreachableException : Exception;
}
