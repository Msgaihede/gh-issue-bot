using DiscordGithubBot.Configuration;
using DiscordGithubBot.Discord;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordGithubBot.Tests.Discord;

public class AppAccessTests
{
    private const ulong User = 77UL;
    private const ulong AlphaServer = 1UL, BetaServer = 2UL, GammaServer = 3UL, UnconfiguredServer = 42UL;

    private readonly IGuildMembership _membership = Substitute.For<IGuildMembership>();
    private readonly AppAccess _sut;

    public AppAccessTests()
    {
        var options = new BotOptions
        {
            Apps =
            [
                new AppConfig { Name = "Alpha", Repo = "owner/alpha", GuildIds = [AlphaServer] },
                new AppConfig { Name = "Beta", Repo = "owner/beta", GuildIds = [BetaServer] },
                // Shares Beta's server and has one of its own.
                new AppConfig { Name = "Gamma", Repo = "owner/gamma", GuildIds = [BetaServer, GammaServer] },
            ],
        };
        _sut = new AppAccess(options, _membership, NullLogger<AppAccess>.Instance);
    }

    private void MemberOf(params ulong[] servers)
    {
        foreach (var server in servers)
            _membership.IsMemberAsync(server, User, Arg.Any<CancellationToken>()).Returns(true);
    }

    private static string[] Names(AppAccessResult result) => result.Apps.Select(a => a.Name).ToArray();

    [Fact]
    public async Task A_configured_server_offers_exactly_its_own_apps_without_asking_discord()
    {
        var result = await _sut.ForAsync(BetaServer, User);

        Assert.Equal(new[] { "Beta", "Gamma" }, Names(result));
        Assert.Null(result.Error);
        await _membership.DidNotReceiveWithAnyArgs().IsMemberAsync(default, default, default);
    }

    [Fact]
    public async Task A_dm_offers_the_apps_of_the_configured_servers_the_user_is_in()
    {
        MemberOf(GammaServer);

        var result = await _sut.ForAsync(null, User);

        Assert.Equal(new[] { "Gamma" }, Names(result));
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task An_unconfigured_server_is_treated_like_a_dm()
    {
        MemberOf(AlphaServer);

        Assert.Equal(new[] { "Alpha" }, Names(await _sut.ForAsync(UnconfiguredServer, User)));
    }

    [Fact]
    public async Task Each_configured_server_is_looked_up_once()
    {
        await _sut.ForAsync(null, User);

        await _membership.Received(1).IsMemberAsync(BetaServer, User, Arg.Any<CancellationToken>());
        await _membership.Received(3).IsMemberAsync(Arg.Any<ulong>(), User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_user_in_none_of_the_servers_gets_no_apps_and_is_told_why()
    {
        var result = await _sut.ForAsync(null, User);

        Assert.Empty(result.Apps);
        Assert.Equal(AppAccess.NotAMember, result.Error);
    }

    /// <summary>A server Discord would not answer for costs its apps, not the whole command.</summary>
    [Fact]
    public async Task A_failed_lookup_drops_only_that_servers_apps()
    {
        _membership.IsMemberAsync(AlphaServer, User, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new HttpRequestException("403 Missing Access")));
        MemberOf(BetaServer);

        var result = await _sut.ForAsync(null, User);

        Assert.Equal(new[] { "Beta", "Gamma" }, Names(result));
        Assert.Null(result.Error);
    }

    /// <summary>"You're not in any of them" would be a lie when a server could not be checked.</summary>
    [Fact]
    public async Task With_nothing_found_and_a_lookup_failed_the_user_is_asked_to_retry()
    {
        _membership.IsMemberAsync(AlphaServer, User, Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new HttpRequestException("timeout"));

        var result = await _sut.ForAsync(null, User);

        Assert.Empty(result.Apps);
        Assert.Equal(AppAccess.LookupFailed, result.Error);
    }

    /// <summary><c>/issue</c> must open its modal inside Discord's three seconds; a hanging lookup cannot hold it.</summary>
    [Fact]
    public async Task A_lookup_that_outlives_the_deadline_counts_as_failed()
    {
        var sut = new AppAccess(new BotOptions
        {
            Apps = [new AppConfig { Name = "Alpha", Repo = "owner/alpha", GuildIds = [AlphaServer] }],
        }, _membership, NullLogger<AppAccess>.Instance) { LookupTimeout = TimeSpan.FromMilliseconds(50) };
        _membership.IsMemberAsync(AlphaServer, User, Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<bool>().Task);

        var result = await sut.ForAsync(null, User).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(AppAccess.LookupFailed, result.Error);
    }
}
