using DiscordGithubBot.Configuration;
using DiscordGithubBot.Discord;

namespace DiscordGithubBot.Tests.Discord;

public class GuildRegistrationsTests
{
    private static BotOptions Options(ulong[] shareGuilds, params ulong[][] appGuilds) => new()
    {
        Discord = new DiscordOptions { ShareCommandGuildIds = [.. shareGuilds] },
        Apps = [.. appGuilds.Select((ids, i) => new AppConfig { Name = $"app{i}", Repo = $"acme/app{i}", GuildIds = [.. ids] })],
    };

    [Fact]
    public void Share_guilds_get_the_share_commands_and_other_app_guilds_are_cleared()
    {
        var plan = BotService.GuildRegistrations(Options([1, 3], [1, 2], [2]));

        Assert.Equal([(1UL, true), (2UL, false), (3UL, true)], plan.OrderBy(p => p.GuildId));
    }

    /// <summary>A share server need not host an app: it can be where the button is posted for another server's app.</summary>
    [Fact]
    public void A_share_guild_without_an_app_is_still_registered()
    {
        var plan = BotService.GuildRegistrations(Options([9], [1]));

        Assert.Contains((9UL, true), plan);
    }

    [Fact]
    public void No_share_guilds_means_every_app_guild_is_cleared()
    {
        var plan = BotService.GuildRegistrations(Options([], [1, 2], [2]));

        Assert.Equal([(1UL, false), (2UL, false)], plan.OrderBy(p => p.GuildId));
    }
}
