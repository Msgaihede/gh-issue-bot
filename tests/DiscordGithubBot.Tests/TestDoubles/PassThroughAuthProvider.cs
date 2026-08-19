using DiscordGithubBot.Configuration;
using DiscordGithubBot.GitHub;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>
/// The PAT half of <see cref="IGitHubAuthProvider"/> and nothing else: hands back the configured token
/// verbatim, so tests of the GitHub clients keep asserting on the token they configured rather than on a
/// minted one. The App half has its own suite in <c>GitHubAuthProviderTests</c>.
/// </summary>
public sealed class PassThroughAuthProvider : IGitHubAuthProvider
{
    public Task<string> GetTokenAsync(AppConfig app, CancellationToken ct = default) =>
        Task.FromResult(app.GitHubToken);
}
