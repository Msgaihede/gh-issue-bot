using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordGithubBot.Configuration;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.GitHub;

/// <summary>An image that now lives at a permanent, GitHub-hosted URL.</summary>
public sealed record UploadedImage(string FileName, string Url);

public interface IImageUploader
{
    /// <returns>null when every strategy failed — callers must treat this as "note the failure, continue".</returns>
    Task<UploadedImage?> UploadAsync(AppConfig app, string fileName, string contentType, byte[] bytes, CancellationToken ct = default);
}

/// <summary>
/// Uploads images to GitHub in two tiers. Tier 1 is the unofficial user-attachments endpoint behind the
/// web UI's drag-and-drop: permanent URLs that render inline in public and private repos. It is
/// undocumented, so any failure there is a warning and a fall-through, never fatal. Tier 2 is the official
/// Contents API, committing the image to an <c>issue-assets</c> branch and linking raw.githubusercontent.com.
/// </summary>
public sealed class GitHubImageUploader(HttpClient http, ILogger<GitHubImageUploader> logger) : IImageUploader
{
    private const string AssetsBranch = "issue-assets";
    private const string UploadsEndpoint = "https://uploads.github.com/user-attachments/assets";

    /// <summary>Marker that identifies the asset URL inside the undocumented upload response.</summary>
    private const string AssetUrlMarker = "user-attachments/assets";

    /// <summary>Response properties known to carry the asset URL, checked before the generic scan.</summary>
    private static readonly string[] AssetUrlProperties = ["href", "url", "asset_url"];

    /// <summary>Repository ids by "owner/repo"; a repository's numeric id never changes.</summary>
    private readonly ConcurrentDictionary<string, long> _repositoryIds = new(StringComparer.OrdinalIgnoreCase);

    public async Task<UploadedImage?> UploadAsync(
        AppConfig app, string fileName, string contentType, byte[] bytes, CancellationToken ct = default)
    {
        try
        {
            return await UploadToUserAttachmentsAsync(app, fileName, contentType, bytes, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Uploading {FileName} to the user-attachments endpoint for {Repo} failed; " +
                "falling back to the Contents API.", fileName, app.Repo);
        }

        try
        {
            return await UploadToContentsApiAsync(app, fileName, bytes, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Uploading {FileName} for {Repo} failed on every strategy; " +
                "the caller continues without the image.", fileName, app.Repo);
            return null;
        }
    }

    /// <summary>Tier 1: the unofficial endpoint. Absolute URL, so the client's base address does not apply.</summary>
    private async Task<UploadedImage> UploadToUserAttachmentsAsync(
        AppConfig app, string fileName, string contentType, byte[] bytes, CancellationToken ct)
    {
        var repositoryId = await GetRepositoryIdAsync(app, ct);
        var url = $"{UploadsEndpoint}?name={Uri.EscapeDataString(fileName)}&repository_id={repositoryId}";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", app.GitHubToken);
        req.Content = new ByteArrayContent(bytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync(ct);
        var assetUrl = FindAssetUrl(body)
            ?? throw new HttpRequestException($"The user-attachments response for {fileName} carried no asset URL.");
        return new UploadedImage(fileName, assetUrl);
    }

    /// <summary>
    /// The response shape is undocumented and has changed before, so parse leniently: prefer the properties
    /// that have carried the URL, then fall back to any root-level string that looks like an asset URL.
    /// </summary>
    private static string? FindAssetUrl(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in AssetUrlProperties)
            if (root.TryGetProperty(name, out var preferred) && IsAssetUrl(preferred, out var preferredUrl))
                return preferredUrl;

        foreach (var property in root.EnumerateObject())
            if (IsAssetUrl(property.Value, out var url))
                return url;

        return null;
    }

    private static bool IsAssetUrl(JsonElement element, [NotNullWhen(true)] out string? url)
    {
        url = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (url is not null && url.Contains(AssetUrlMarker, StringComparison.Ordinal)) return true;

        url = null;
        return false;
    }

    /// <summary>Tier 2: commit the image to the assets branch and link it through raw.githubusercontent.com.</summary>
    private async Task<UploadedImage> UploadToContentsApiAsync(
        AppConfig app, string fileName, byte[] bytes, CancellationToken ct)
    {
        await EnsureAssetsBranchAsync(app, ct);

        // A timestamp prefix makes every upload a fresh path, so the existing-file SHA is never needed.
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var path = $"{AssetsBranch}/{stamp}-{Sanitize(fileName)}";

        using var resp = await SendAsync(
            app, HttpMethod.Put, $"repos/{app.Repo}/contents/{path}",
            new PutContentsPayload("chore: add issue screenshot", Convert.ToBase64String(bytes), AssetsBranch), ct);

        // raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}: ref and folder are both "issue-assets".
        return new UploadedImage(fileName, $"https://raw.githubusercontent.com/{app.Repo}/{AssetsBranch}/{path}");
    }

    /// <summary>Creates the assets branch off the default branch's HEAD when it does not exist yet.</summary>
    private async Task EnsureAssetsBranchAsync(AppConfig app, CancellationToken ct)
    {
        using (var probe = await SendRawAsync(
            app, HttpMethod.Get, $"repos/{app.Repo}/branches/{AssetsBranch}", payload: null, ct))
        {
            if (probe.IsSuccessStatusCode) return;
            if (probe.StatusCode != HttpStatusCode.NotFound) probe.EnsureSuccessStatusCode();
        }

        var repo = await GetJsonAsync<RepositoryDto>(app, $"repos/{app.Repo}", ct);
        if (string.IsNullOrWhiteSpace(repo.DefaultBranch))
            throw new HttpRequestException($"GitHub reported no default branch for {app.Repo}.");

        var head = await GetJsonAsync<GitRefDto>(app, $"repos/{app.Repo}/git/ref/heads/{repo.DefaultBranch}", ct);
        var sha = head.Object?.Sha;
        if (string.IsNullOrWhiteSpace(sha))
            throw new HttpRequestException($"GitHub reported no HEAD commit for {app.Repo}@{repo.DefaultBranch}.");

        using var created = await SendAsync(
            app, HttpMethod.Post, $"repos/{app.Repo}/git/refs",
            new CreateRefPayload($"refs/heads/{AssetsBranch}", sha), ct);
    }

    private async Task<long> GetRepositoryIdAsync(AppConfig app, CancellationToken ct)
    {
        if (_repositoryIds.TryGetValue(app.Repo, out var cached)) return cached;

        var repo = await GetJsonAsync<RepositoryDto>(app, $"repos/{app.Repo}", ct);
        if (repo.Id <= 0) throw new HttpRequestException($"GitHub reported no repository id for {app.Repo}.");

        _repositoryIds[app.Repo] = repo.Id;
        return repo.Id;
    }

    /// <summary>Keeps only the characters that are safe in both a git path and a URL.</summary>
    private static string Sanitize(string fileName)
    {
        var sanitized = new string([.. fileName.Select(
            c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);
        return sanitized.Length == 0 ? "image" : sanitized;
    }

    private async Task<T> GetJsonAsync<T>(AppConfig app, string path, CancellationToken ct) where T : class
    {
        using var resp = await SendAsync(app, HttpMethod.Get, path, payload: null, ct);
        return await resp.Content.ReadFromJsonAsync<T>(ct)
            ?? throw new HttpRequestException($"GitHub returned an empty body for {path}.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        AppConfig app, HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        var resp = await SendRawAsync(app, method, path, payload, ct);
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

    private async Task<HttpResponseMessage> SendRawAsync(
        AppConfig app, HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", app.GitHubToken);
        if (payload is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        return await http.SendAsync(req, ct);
    }

    private sealed record PutContentsPayload(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("branch")] string Branch);

    private sealed record CreateRefPayload(
        [property: JsonPropertyName("ref")] string Ref,
        [property: JsonPropertyName("sha")] string Sha);

    private sealed class RepositoryDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
    }

    private sealed class GitRefDto
    {
        [JsonPropertyName("object")] public GitObjectDto? Object { get; set; }

        internal sealed class GitObjectDto
        {
            [JsonPropertyName("sha")] public string? Sha { get; set; }
        }
    }
}
