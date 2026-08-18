using DiscordGithubBot.Configuration;
using Microsoft.Extensions.Configuration;

namespace DiscordGithubBot.Tests.Configuration;

public class BotOptionsTests
{
    private static BotOptions Valid() => new()
    {
        Discord = new() { Token = "t" },
        OpenAI = new() { ApiKey = "k" },
        Apps =
        [
            new AppConfig
            {
                Name = "MyApp", Repo = "owner/repo", GitHubToken = "pat",
                GuildIds = [1UL], ChannelIds = [2UL],
            },
        ],
    };

    [Fact]
    public void Valid_options_produce_no_errors() => Assert.Empty(Valid().Validate());

    [Fact]
    public void Missing_discord_token_is_reported()
    {
        var o = Valid(); o.Discord.Token = "";
        Assert.Contains(o.Validate(), e => e.Contains("Discord:Token"));
    }

    [Fact]
    public void Missing_openai_key_is_reported()
    {
        var o = Valid(); o.OpenAI.ApiKey = "";
        Assert.Contains(o.Validate(), e => e.Contains("OpenAI:ApiKey"));
    }

    [Fact]
    public void No_apps_is_reported()
    {
        var o = Valid(); o.Apps.Clear();
        Assert.Contains(o.Validate(), e => e.Contains("Apps"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("norepo")]
    [InlineData("owner/repo/extra")]
    public void Bad_repo_format_is_reported(string repo)
    {
        var o = Valid(); o.Apps[0].Repo = repo;
        Assert.Contains(o.Validate(), e => e.Contains("Repo"));
    }

    [Fact]
    public void Duplicate_repo_is_reported()
    {
        var o = Valid();
        o.Apps.Add(new AppConfig
        {
            Name = "Other", Repo = "owner/repo", GitHubToken = "pat2",
            GuildIds = [3UL], ChannelIds = [4UL],
        });
        Assert.Contains(o.Validate(), e => e.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void App_without_guilds_channels_token_or_name_is_reported()
    {
        var o = Valid();
        o.Apps[0].GuildIds.Clear(); o.Apps[0].ChannelIds.Clear();
        o.Apps[0].GitHubToken = ""; o.Apps[0].Name = "";
        var errors = o.Validate();
        Assert.Contains(errors, e => e.Contains("GuildIds"));
        Assert.Contains(errors, e => e.Contains("ChannelIds"));
        Assert.Contains(errors, e => e.Contains("GitHubToken"));
        Assert.Contains(errors, e => e.Contains("Name"));
    }

    [Fact]
    public void AppsForGuild_filters_by_guild()
    {
        var o = Valid();
        o.Apps.Add(new AppConfig
        {
            Name = "B", Repo = "owner/other", GitHubToken = "p",
            GuildIds = [9UL], ChannelIds = [2UL],
        });
        Assert.Single(o.AppsForGuild(1UL));
        Assert.Equal("owner/other", Assert.Single(o.AppsForGuild(9UL)).Repo);
        Assert.Empty(o.AppsForGuild(42UL));
    }

    [Fact]
    public void AppByRepo_finds_exact_repo()
    {
        Assert.NotNull(Valid().AppByRepo("owner/repo"));
        Assert.Null(Valid().AppByRepo("owner/none"));
    }

    [Fact]
    public void Binds_from_configuration_including_env_style_keys()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Discord:Token"] = "tok",
            ["OpenAI:ApiKey"] = "key",
            ["Apps:0:Name"] = "MyApp",
            ["Apps:0:Repo"] = "owner/repo",
            ["Apps:0:GitHubToken"] = "pat",
            ["Apps:0:GuildIds:0"] = "111111111111111111",
            ["Apps:0:ChannelIds:0"] = "222222222222222222",
        }).Build();
        var o = config.Get<BotOptions>()!;
        Assert.Empty(o.Validate());
        Assert.Equal(111111111111111111UL, o.Apps[0].GuildIds[0]);
        Assert.Equal("gpt-5.6-luna", o.OpenAI.ChatModel); // default survives binding
    }
}
