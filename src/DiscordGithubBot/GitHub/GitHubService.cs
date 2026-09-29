using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordGithubBot.Configuration;

namespace DiscordGithubBot.GitHub;

/// <summary>A label defined in the repository; the description is "" when the repo gives none.</summary>
public sealed record RepoLabel(string Name, string Description);

/// <summary>The tip of a repository's default branch: the commit, and the root tree it points at.</summary>
public sealed record BranchHead(string Branch, string CommitSha, string TreeSha);

/// <summary>A file in a git tree. <see cref="Size"/> is in bytes.</summary>
public sealed record TreeFile(string Path, string BlobSha, long Size);

/// <param name="Truncated">GitHub stops a recursive listing past 100,000 entries or 7 MB and says so here.</param>
public sealed record RepoTree(IReadOnlyList<TreeFile> Files, bool Truncated);

/// <summary>A GitHub issue as the bot uses it; timestamps are always UTC.</summary>
public sealed record GitHubIssue(
    int Number, string Title, string Body, string State,
    DateTime UpdatedAtUtc, DateTime? ClosedAtUtc, string HtmlUrl);

public interface IGitHubService
{
    Task<GitHubIssue> CreateIssueAsync(
        AppConfig app, string title, string body, IReadOnlyList<string> labels, CancellationToken ct = default);

    /// <returns>The html_url of the created comment.</returns>
    Task<string> AddCommentAsync(AppConfig app, int issueNumber, string body, CancellationToken ct = default);

    /// <summary>Every label the repository defines, in GitHub's order.</summary>
    Task<IReadOnlyList<RepoLabel>> ListLabelsAsync(AppConfig app, CancellationToken ct = default);

    /// <param name="state">"open" | "closed" | "all"</param>
    /// <param name="sinceUtc">maps to the GitHub 'since' query param (updated-at filter) when set</param>
    Task<IReadOnlyList<GitHubIssue>> ListIssuesAsync(AppConfig app, string state, DateTime? sinceUtc, CancellationToken ct = default);

    /// <summary>The default branch and the commit and tree at its tip.</summary>
    Task<BranchHead> GetDefaultBranchHeadAsync(AppConfig app, CancellationToken ct = default);

    /// <summary>Every file (blob) under a tree, recursively; submodules and directories are left out.</summary>
    Task<RepoTree> GetTreeAsync(AppConfig app, string treeSha, CancellationToken ct = default);

    /// <summary>A blob's contents as text, cut to <paramref name="maxChars"/>; null when the blob is binary.</summary>
    Task<string?> GetBlobTextAsync(AppConfig app, string blobSha, int maxChars, CancellationToken ct = default);
}

/// <summary>
/// GitHub REST client: issues and labels, plus the read-only code access the repository map needs. The
/// shared <see cref="HttpClient"/> carries the base address and the static headers; the per-app bearer
/// token is attached to every request because it differs per configured app — and, for a GitHub App,
/// expires and is re-minted by <see cref="IGitHubAuthProvider"/>.
/// </summary>
public sealed class GitHubService(HttpClient http, IGitHubAuthProvider auth) : IGitHubService
{
    /// <summary>GitHub's maximum page size for the issues endpoint.</summary>
    private const int PerPage = 100;

    public async Task<GitHubIssue> CreateIssueAsync(
        AppConfig app, string title, string body, IReadOnlyList<string> labels, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            app, HttpMethod.Post, $"repos/{app.Repo}/issues",
            new CreateIssuePayload(title, body, labels.ToArray()), ct);
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

    public async Task<IReadOnlyList<RepoLabel>> ListLabelsAsync(AppConfig app, CancellationToken ct = default)
    {
        var labels = new List<RepoLabel>();
        for (var page = 1; ; page++)
        {
            using var resp = await SendAsync(
                app, HttpMethod.Get, $"repos/{app.Repo}/labels?per_page={PerPage}&page={page}", payload: null, ct);
            var dtos = await ReadJsonAsync<List<LabelDto>>(resp, ct);

            labels.AddRange(dtos
                .Where(d => !string.IsNullOrWhiteSpace(d.Name))
                .Select(d => new RepoLabel(d.Name!, d.Description ?? "")));

            if (dtos.Count < PerPage) return labels;
        }
    }

    public async Task<BranchHead> GetDefaultBranchHeadAsync(AppConfig app, CancellationToken ct = default)
    {
        string branch;
        using (var resp = await SendAsync(app, HttpMethod.Get, $"repos/{app.Repo}", payload: null, ct))
        {
            branch = (await ReadJsonAsync<RepoDto>(resp, ct)).DefaultBranch
                ?? throw new HttpRequestException($"GitHub returned no default branch for {app.Repo}.");
        }

        using var head = await SendAsync(
            app, HttpMethod.Get, $"repos/{app.Repo}/branches/{Uri.EscapeDataString(branch)}", payload: null, ct);
        var dto = await ReadJsonAsync<BranchDto>(head, ct);

        return new BranchHead(branch,
            dto.Commit?.Sha ?? throw new HttpRequestException($"GitHub returned no head commit for {app.Repo}@{branch}."),
            dto.Commit.Commit?.Tree?.Sha ?? throw new HttpRequestException($"GitHub returned no tree for {app.Repo}@{branch}."));
    }

    public async Task<RepoTree> GetTreeAsync(AppConfig app, string treeSha, CancellationToken ct = default)
    {
        using var resp = await SendAsync(
            app, HttpMethod.Get, $"repos/{app.Repo}/git/trees/{treeSha}?recursive=1", payload: null, ct);
        var dto = await ReadJsonAsync<TreeDto>(resp, ct);

        var files = (dto.Tree ?? [])
            .Where(e => e.Type == "blob" && !string.IsNullOrEmpty(e.Path) && !string.IsNullOrEmpty(e.Sha))
            .Select(e => new TreeFile(e.Path!, e.Sha!, e.Size ?? 0))
            .ToList();

        return new RepoTree(files, dto.Truncated);
    }

    public async Task<string?> GetBlobTextAsync(AppConfig app, string blobSha, int maxChars, CancellationToken ct = default)
    {
        using var resp = await SendAsync(app, HttpMethod.Get, $"repos/{app.Repo}/git/blobs/{blobSha}", payload: null, ct);
        var dto = await ReadJsonAsync<BlobDto>(resp, ct);

        var bytes = dto.Encoding == "base64"
            ? Convert.FromBase64String((dto.Content ?? "").Replace("\n", ""))
            : Encoding.UTF8.GetBytes(dto.Content ?? "");

        // A NUL byte is the cheap, reliable tell of a binary file; text files essentially never contain one.
        if (Array.IndexOf(bytes, (byte)0) >= 0) return null;

        var text = Encoding.UTF8.GetString(bytes);
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private async Task<HttpResponseMessage> SendAsync(
        AppConfig app, HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetTokenAsync(app, ct));
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

    private sealed class RepoDto
    {
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
    }

    private sealed class BranchDto
    {
        [JsonPropertyName("commit")] public BranchCommitDto? Commit { get; set; }
    }

    private sealed class BranchCommitDto
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("commit")] public CommitDto? Commit { get; set; }
    }

    private sealed class CommitDto
    {
        [JsonPropertyName("tree")] public ShaDto? Tree { get; set; }
    }

    private sealed class ShaDto
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
    }

    private sealed class TreeDto
    {
        [JsonPropertyName("tree")] public List<TreeEntryDto>? Tree { get; set; }
        [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    }

    private sealed class TreeEntryDto
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
    }

    private sealed class BlobDto
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("encoding")] public string? Encoding { get; set; }
    }

    private sealed class LabelDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    private sealed class CommentDto
    {
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    }
}
