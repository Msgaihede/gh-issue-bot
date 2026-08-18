using System.Net;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>Scripted HTTP handler: routes match in registration order, records requests.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    public sealed record Recorded(HttpMethod Method, string Url, string? Body, string? AuthHeader);

    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = new();
    public List<Recorded> Requests { get; } = new();

    public void When(HttpMethod method, string urlContains, HttpStatusCode status, string jsonBody) =>
        _routes.Add((
            req => req.Method == method && req.RequestUri!.ToString().Contains(urlContains),
            _ => new HttpResponseMessage(status)
            {
                Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
            }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Recorded(request.Method, request.RequestUri!.ToString(), body,
            request.Headers.Authorization?.ToString()));
        var route = _routes.FirstOrDefault(r => r.Match(request));
        return route.Respond is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
            : route.Respond(request);
    }

    public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("https://api.github.com/") };
}
