using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using DiscordGithubBot.Configuration;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.GitHub;

public interface IGitHubAuthProvider
{
    /// <summary>Bearer token for this app: the PAT verbatim, or a cached/refreshed installation token.</summary>
    Task<string> GetTokenAsync(AppConfig app, CancellationToken ct = default);
}

/// <summary>
/// Turns an app's configured credentials into the bearer token every GitHub call carries. A PAT is that
/// token already. A GitHub App is not: the private key signs a short-lived RS256 JWT, the JWT buys an
/// installation access token that GitHub expires after an hour, and that token is what the REST calls use.
/// Tokens are cached per app and minted again shortly before they expire, so an hour of reports costs one
/// exchange rather than one per interaction.
/// </summary>
/// <remarks>
/// The JWT is built by hand — three base64url segments and one <see cref="RSA.SignData(byte[], HashAlgorithmName, RSASignaturePadding)"/>
/// call — rather than by taking a JWT library as a dependency for a token shape that has three claims and
/// has not changed since GitHub Apps shipped.
/// </remarks>
public sealed class GitHubAuthProvider(HttpClient http, ILogger<GitHubAuthProvider> logger) : IGitHubAuthProvider
{
    /// <summary>
    /// JWT lifetime. GitHub rejects anything over 10 minutes, and it validates <c>iat</c> against its own
    /// clock — so the window is backdated by <see cref="ClockSkewSeconds"/> rather than started at "now",
    /// which is what makes a slightly fast local clock a non-event instead of a 401.
    /// </summary>
    private const int JwtLifetimeSeconds = 600;

    private const int ClockSkewSeconds = 60;

    /// <summary>
    /// How far ahead of <c>expires_at</c> a cached token is treated as spent. GitHub's hour is generous;
    /// five minutes covers a slow request that starts just inside the window and lands just outside it.
    /// </summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Validity assumed when GitHub returns a token without an <c>expires_at</c>. Documented as always
    /// present, so this is the shape-changed case: keep working, but re-mint often enough that a token
    /// whose real lifetime we never learned cannot go stale in the cache.
    /// </summary>
    private static readonly TimeSpan FallbackValidity = TimeSpan.FromMinutes(10);

    /// <summary>Per-app credential state, keyed by "owner/repo" — the same key <see cref="BotOptions"/> makes unique.</summary>
    private readonly ConcurrentDictionary<string, AppState> _states = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> GetTokenAsync(AppConfig app, CancellationToken ct = default)
    {
        // A PAT is already the bearer token; there is nothing to mint, cache or await.
        if (app.GitHubApp is not { } auth) return app.GitHubToken;

        var state = _states.GetOrAdd(app.Repo, _ => new AppState());
        if (Usable(state.Token) is { } cached) return cached;

        // One mint per app at a time: a burst of interactions on the same repo would otherwise each see an
        // empty cache and each buy their own token from GitHub.
        await state.Gate.WaitAsync(ct);
        try
        {
            if (Usable(state.Token) is { } justMinted) return justMinted;

            var minted = await MintAsync(app, auth, state, ct);
            state.Token = minted;
            return minted.Token;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static string? Usable(CachedToken? token) =>
        token is not null && DateTimeOffset.UtcNow < token.ExpiresAt - RefreshMargin ? token.Token : null;

    private async Task<CachedToken> MintAsync(
        AppConfig app, GitHubAppAuth auth, AppState state, CancellationToken ct)
    {
        var jwt = CreateJwt(auth.AppId, state.Pem(auth));

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"app/installations/{auth.InstallationId}/access_tokens");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var dto = await resp.Content.ReadFromJsonAsync<InstallationTokenDto>(ct);
        if (string.IsNullOrWhiteSpace(dto?.Token))
            throw new HttpRequestException(
                $"GitHub accepted the installation-token request for {app.Repo} but returned no token.");

        var expiresAt = dto.ExpiresAt;
        if (expiresAt is null)
            logger.LogWarning(
                "GitHub returned an installation token for {Repo} with no expires_at; assuming {Minutes} minutes.",
                app.Repo, FallbackValidity.TotalMinutes);

        var expiry = expiresAt ?? DateTimeOffset.UtcNow + FallbackValidity;
        logger.LogInformation(
            "Minted a GitHub App installation token for {Repo} (app {AppId}, installation {InstallationId}); " +
            "it expires at {ExpiresAt:u}.", app.Repo, auth.AppId, auth.InstallationId, expiry);

        return new CachedToken(dto.Token, expiry);
    }

    /// <summary>
    /// The App-authentication JWT: <c>{"alg":"RS256","typ":"JWT"}</c> over <c>{iat, exp, iss}</c>, signed
    /// with the App's private key. <c>iss</c> is the App id — the installation id belongs to the exchange
    /// request, not to the JWT.
    /// </summary>
    private static string CreateJwt(long appId, string pem)
    {
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ClockSkewSeconds;
        var header = """{"alg":"RS256","typ":"JWT"}""";
        var payload = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"iat":{{issuedAt}},"exp":{{issuedAt + JwtLifetimeSeconds}},"iss":{{appId}}}""");

        var signingInput =
            $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header))}." +
            $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    /// <summary>
    /// Everything the provider remembers about one configured app. Only ever mutated while its own
    /// <see cref="Gate"/> is held; <see cref="Token"/> is volatile because the fast path reads it without.
    /// </summary>
    private sealed class AppState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public volatile CachedToken? Token;

        private string? _pem;

        /// <summary>The private key, read from disk at most once per process.</summary>
        public string Pem(GitHubAppAuth auth) =>
            _pem ??= string.IsNullOrWhiteSpace(auth.PrivateKeyPath)
                ? auth.PrivateKey
                : File.ReadAllText(auth.PrivateKeyPath);
    }

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt);

    private sealed class InstallationTokenDto
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
    }
}
