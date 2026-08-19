using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.GitHub;

/// <summary>
/// The key is generated per test run rather than checked in: a real GitHub App private key in the
/// repository would be a leak, and a fake one would not verify a signature.
/// </summary>
public class GitHubAuthProviderTests : IDisposable
{
    private const long AppId = 12345;
    private const long InstallationId = 987654;

    private readonly RSA _key = RSA.Create(2048);

    public void Dispose() => _key.Dispose();

    private static AppConfig PatApp() => new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "PAT123",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    private AppConfig GitHubApp(string? pem = null, string privateKeyPath = "") => new()
    {
        Name = "MyApp", Repo = "owner/repo", GuildIds = [1UL], ChannelIds = [2UL],
        GitHubApp = new GitHubAppAuth
        {
            AppId = AppId,
            InstallationId = InstallationId,
            PrivateKey = privateKeyPath.Length > 0 ? "" : pem ?? _key.ExportRSAPrivateKeyPem(),
            PrivateKeyPath = privateKeyPath,
        },
    };

    private static GitHubAuthProvider Provider(FakeHttpMessageHandler fake) =>
        new(fake.CreateClient(), NullLogger<GitHubAuthProvider>.Instance);

    private static string TokenResponse(string token, TimeSpan validFor)
    {
        var expiresAt = (DateTimeOffset.UtcNow + validFor).ToString("o", CultureInfo.InvariantCulture);
        return $$"""{"token":"{{token}}","expires_at":"{{expiresAt}}"}""";
    }

    private static string ExchangePath => $"app/installations/{InstallationId}/access_tokens";

    /// <summary>One base64url JWT segment, back to the JSON it encodes.</summary>
    private static string Decode(string segment) => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(segment));

    [Fact]
    public async Task A_pat_app_gets_its_token_back_without_talking_to_github()
    {
        var fake = new FakeHttpMessageHandler();

        Assert.Equal("PAT123", await Provider(fake).GetTokenAsync(PatApp()));

        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task An_app_exchanges_a_jwt_for_an_installation_token()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, ExchangePath, HttpStatusCode.Created,
            TokenResponse("ghs_installation", TimeSpan.FromHours(1)));

        var token = await Provider(fake).GetTokenAsync(GitHubApp());

        Assert.Equal("ghs_installation", token);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task The_exchange_carries_a_well_formed_rs256_jwt_signed_by_the_app_key()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, ExchangePath, HttpStatusCode.Created,
            TokenResponse("ghs_x", TimeSpan.FromHours(1)));
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await Provider(fake).GetTokenAsync(GitHubApp());

        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var authHeader = Assert.Single(fake.Requests).AuthHeader;
        Assert.StartsWith("Bearer ", authHeader);
        var segments = authHeader!["Bearer ".Length..].Split('.');
        Assert.Equal(3, segments.Length);

        using var header = JsonDocument.Parse(Decode(segments[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());

        using var payload = JsonDocument.Parse(Decode(segments[1]));
        var iat = payload.RootElement.GetProperty("iat").GetInt64();
        var exp = payload.RootElement.GetProperty("exp").GetInt64();
        Assert.Equal(AppId, payload.RootElement.GetProperty("iss").GetInt64());
        Assert.Equal(600, exp - iat);                  // GitHub rejects anything over ten minutes
        // Backdated by exactly the 60 s clock-skew allowance from whenever the JWT was minted, which
        // is somewhere between `before` and `after`. Anchoring on `before` alone flaked on CI: a cold
        // runner spends seconds on JIT and RSA before minting, pushing iat past a ±1 s window.
        Assert.InRange(iat, before - 60, after - 60);

        // The signature is the whole point: a JWT GitHub cannot verify is a 401 nobody sees until prod.
        Assert.True(_key.VerifyData(
            Encoding.UTF8.GetBytes($"{segments[0]}.{segments[1]}"),
            Base64Url.DecodeFromChars(segments[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task A_pkcs8_private_key_works_too()
    {
        // GitHub hands out PKCS#1 ("BEGIN RSA PRIVATE KEY"), but conversion to PKCS#8 is a common step
        // in key-management tooling and ImportFromPem accepts both.
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, ExchangePath, HttpStatusCode.Created,
            TokenResponse("ghs_pkcs8", TimeSpan.FromHours(1)));

        Assert.Equal("ghs_pkcs8",
            await Provider(fake).GetTokenAsync(GitHubApp(pem: _key.ExportPkcs8PrivateKeyPem())));
    }

    [Fact]
    public async Task A_token_still_inside_its_validity_is_reused()
    {
        var fake = new FakeHttpMessageHandler();
        fake.WhenSequence(HttpMethod.Post, ExchangePath, HttpStatusCode.Created,
            TokenResponse("first", TimeSpan.FromHours(1)),
            TokenResponse("second", TimeSpan.FromHours(1)));
        var provider = Provider(fake);
        var app = GitHubApp();

        Assert.Equal("first", await provider.GetTokenAsync(app));
        Assert.Equal("first", await provider.GetTokenAsync(app));

        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task A_token_inside_the_refresh_margin_is_replaced()
    {
        // Four minutes left is inside the five-minute margin: usable by GitHub's clock, spent by ours.
        var fake = new FakeHttpMessageHandler();
        fake.WhenSequence(HttpMethod.Post, ExchangePath, HttpStatusCode.Created,
            TokenResponse("stale", TimeSpan.FromMinutes(4)),
            TokenResponse("fresh", TimeSpan.FromHours(1)));
        var provider = Provider(fake);
        var app = GitHubApp();

        Assert.Equal("stale", await provider.GetTokenAsync(app));
        Assert.Equal("fresh", await provider.GetTokenAsync(app));

        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task An_already_expired_token_is_replaced()
    {
        var fake = new FakeHttpMessageHandler();
        fake.WhenSequence(HttpMethod.Post, ExchangePath, HttpStatusCode.Created,
            TokenResponse("expired", TimeSpan.FromMinutes(-1)),
            TokenResponse("fresh", TimeSpan.FromHours(1)));
        var provider = Provider(fake);
        var app = GitHubApp();

        Assert.Equal("expired", await provider.GetTokenAsync(app));
        Assert.Equal("fresh", await provider.GetTokenAsync(app));
    }

    [Fact]
    public async Task Concurrent_callers_buy_one_token_between_them()
    {
        var fake = new FakeHttpMessageHandler();
        fake.WhenSequence(HttpMethod.Post, ExchangePath, HttpStatusCode.Created,
            TokenResponse("only", TimeSpan.FromHours(1)),
            TokenResponse("stampede", TimeSpan.FromHours(1)));
        var provider = Provider(fake);
        var app = GitHubApp();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => provider.GetTokenAsync(app)));

        Assert.All(tokens, t => Assert.Equal("only", t));
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Two_apps_keep_separate_tokens()
    {
        var fake = new FakeHttpMessageHandler();
        fake.WhenSequence(HttpMethod.Post, ExchangePath, HttpStatusCode.Created,
            TokenResponse("for-first", TimeSpan.FromHours(1)),
            TokenResponse("for-second", TimeSpan.FromHours(1)));
        var provider = Provider(fake);
        var first = GitHubApp();
        var second = GitHubApp();
        second.Repo = "owner/other";

        Assert.Equal("for-first", await provider.GetTokenAsync(first));
        Assert.Equal("for-second", await provider.GetTokenAsync(second));
        Assert.Equal("for-first", await provider.GetTokenAsync(first));

        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task A_key_file_is_read_once_and_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gh-app-key-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(path, _key.ExportRSAPrivateKeyPem());

        var fake = new FakeHttpMessageHandler();
        fake.WhenSequence(HttpMethod.Post, ExchangePath, HttpStatusCode.Created,
            TokenResponse("from-file", TimeSpan.FromMinutes(4)),
            TokenResponse("still-from-file", TimeSpan.FromHours(1)));
        var provider = Provider(fake);
        var app = GitHubApp(privateKeyPath: path);

        Assert.Equal("from-file", await provider.GetTokenAsync(app));

        // The second mint has no file to read from; it works because the PEM was kept from the first.
        File.Delete(path);
        Assert.Equal("still-from-file", await provider.GetTokenAsync(app));
    }

    [Fact]
    public async Task A_rejected_exchange_surfaces_as_an_http_error()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, ExchangePath, HttpStatusCode.Unauthorized, "{}");

        await Assert.ThrowsAsync<HttpRequestException>(() => Provider(fake).GetTokenAsync(GitHubApp()));
    }

    [Fact]
    public async Task An_exchange_response_without_a_token_is_an_error_not_an_empty_bearer()
    {
        var fake = new FakeHttpMessageHandler();
        fake.When(HttpMethod.Post, ExchangePath, HttpStatusCode.Created, """{"expires_at":"2026-08-19T12:00:00Z"}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider(fake).GetTokenAsync(GitHubApp()));

        Assert.Contains("owner/repo", ex.Message);
    }

    [Fact]
    public async Task A_token_without_an_expiry_is_used_but_not_trusted_for_long()
    {
        var fake = new FakeHttpMessageHandler();
        fake.WhenSequence(HttpMethod.Post, ExchangePath, HttpStatusCode.Created,
            """{"token":"undated"}""",
            TokenResponse("dated", TimeSpan.FromHours(1)));
        var provider = Provider(fake);
        var app = GitHubApp();

        // Ten minutes of assumed validity, five of which the refresh margin eats: still cached, and the
        // second call inside that window reuses it rather than minting again.
        Assert.Equal("undated", await provider.GetTokenAsync(app));
        Assert.Equal("undated", await provider.GetTokenAsync(app));

        Assert.Single(fake.Requests);
    }
}
