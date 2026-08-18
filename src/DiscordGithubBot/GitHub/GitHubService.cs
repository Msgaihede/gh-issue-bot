using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordGithubBot.Configuration;

namespace DiscordGithubBot.GitHub;

/// <summary>A GitHub issue as the bot uses it; timestamps are always UTC.</summary>
public sealed record GitHubIssue(
    int Number, string Title, string Body, string State,
    DateTime UpdatedAtUtc, DateTime? ClosedAtUtc, string HtmlUrl);

public interface IGitHubService
{
    Task<GitHubIssue> CreateIssueAsync(AppConfig app, string title, string body, string label, CancellationToken ct = default);

    /// <returns>The html_url of the created comment.</returns>
    Task<string> AddCommentAsync(AppConfig app, int issueNumber, string body, CancellationToken ct = default);

    /// <param name="state">"open" | "closed" | "all"</param>
    /// <param name="sinceUtc">maps to the GitHub 'since' query param (updated-at filter) when set</param>
    Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(AppConfig app, string state, DateTime? sinceUtc, CancellationToken ct = default);
}

/// <summary>
/// GitHub REST issues client. The shared <see cref="HttpClient"/> carries the base address and the
/// static headers; the per-app PAT is attached to every request because it differs per configured app.
/// </summary>
public sealed class GitHubService(HttpClient http) : IGitHubService
{
    /// <summary>GitHub's maximum page size for the issues endpoint.</summary>
    private const int PerPage = 100;

    public async Task<GitHubIssue> CreateIssueAsync(
        AppConfig app, string title, string body, string label, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            app, HttpMethod.Post, $"repos/{app.Repo}/issues",
            new CreateIssuePayload(title, body, [label]), ct);
        return ToIssue(await ReadJsonAsync<IssueDto>(resp, ct));
    }

    public async Task<string> AddCommentAsync(
        AppConfig app, int issueNumber, string body, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            app, HttpMethod.Post, $"repos/{app.Repo}/issues/{issueNumber}/comments",
            new CreateCommentPayload(body), ct);
        // A comment with no html_url is a GitHub response we do not understand. Returning "" would put an
        // empty link in front of the reporter and report success; failing here reaches the retry instead.
        return (await ReadJsonAsync<CommentDto>(resp, ct)).HtmlUrl
            ?? throw new HttpRequestException(
                $"GitHub accepted the comment on {app.Repo}#{issueNumber} but returned no html_url.");
    }

    public async Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(
        AppConfig app, string state, DateTime? sinceUtc, CancellationToken ct = default)
    {
        var since = sinceUtc is null
            ? ""
            : "&since=" + Uri.EscapeDataString(
                sinceUtc.Value.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));

        var issues = new List<GitHubIssue>();
        for (var page = 1; ; page++)
        {
            var path = $"repos/{app.Repo}/issues?state={Uri.EscapeDataString(state)}&per_page={PerPage}&page={page}{since}";
            using var resp = await SendAsync(app, HttpMethod.Get, path, payload: null, ct);
            var dtos = await ReadJsonAsync<List<IssueDto>>(resp, ct);

            // The issues endpoint also returns pull requests; those carry a "pull_request" property.
            issues.AddRange(dtos.Where(d => d.PullRequest is null).Select(ToIssue));

            if (dtos.Count < PerPage) return issues;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        AppConfig app, HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", app.GitHubToken);
        if (payload is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var resp = await http.SendAsync(req, ct);
        try
        {
            resp.EnsureSuccessStatusCode();
        }
        catch
        {
            resp.Dispose();
            throw;
        }
        return resp;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct) where T : class =>
        await resp.Content.ReadFromJsonAsync<T>(ct)
        ?? throw new HttpRequestException($"GitHub returned an empty body for {resp.RequestMessage?.RequestUri}.");

    private static GitHubIssue ToIssue(IssueDto dto) => new(
        dto.Number, dto.Title ?? "", dto.Body ?? "", dto.State ?? "",
        dto.UpdatedAt.UtcDateTime, dto.ClosedAt?.UtcDateTime, dto.HtmlUrl ?? "");

    private sealed record CreateIssuePayload(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("labels")] string[] Labels);

    private sealed record CreateCommentPayload(
        [property: JsonPropertyName("body")] string Body);

    private sealed class IssueDto
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
        [JsonPropertyName("closed_at")] public DateTimeOffset? ClosedAt { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("pull_request")] public JsonElement? PullRequest { get; set; }
    }

    private sealed class CommentDto
    {
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    }
}
