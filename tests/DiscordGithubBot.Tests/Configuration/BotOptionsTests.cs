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

    [Theory]
    [InlineData(" owner/repo")]
    [InlineData("owner/repo\n")]
    [InlineData("\t owner/repo \r\n")]
    public void Repo_is_trimmed_on_assignment(string configured)
    {
        var o = Valid();
        o.Apps[0].Repo = configured;

        Assert.Empty(o.Validate());
        Assert.Equal("owner/repo", o.Apps[0].Repo);
        Assert.NotNull(o.AppByRepo("owner/repo"));
    }

    [Fact]
    public void A_repo_bound_from_a_secret_file_is_trimmed_too()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Apps:0:Repo"] = " owner/repo\n",
        }).Build();

        Assert.Equal("owner/repo", config.Get<BotOptions>()!.Apps[0].Repo);
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

    /// <summary>Stand-in PEM: validation only ever asks whether a key was configured, never parses one.</summary>
    private const string Pem = "-----BEGIN RSA PRIVATE KEY-----not-a-real-key-----END RSA PRIVATE KEY-----";

    /// <summary>A GitHub App app: no PAT, a complete <c>GitHubApp</c> block instead.</summary>
    private static BotOptions AppAuth(Action<GitHubAppAuth>? tweak = null)
    {
        var o = Valid();
        o.Apps[0].GitHubToken = "";
        o.Apps[0].GitHubApp = new GitHubAppAuth
        {
            AppId = 12345, InstallationId = 987654, PrivateKey = Pem,
        };
        tweak?.Invoke(o.Apps[0].GitHubApp!);
        return o;
    }

    [Fact]
    public void A_github_app_block_is_a_valid_alternative_to_a_pat() => Assert.Empty(AppAuth().Validate());

    [Fact]
    public void Configuring_both_a_pat_and_a_github_app_is_reported()
    {
        var o = AppAuth();
        o.Apps[0].GitHubToken = "pat";
        Assert.Contains(o.Validate(), e => e.Contains("not both"));
    }

    [Fact]
    public void Configuring_neither_a_pat_nor_a_github_app_is_reported()
    {
        var o = Valid();
        o.Apps[0].GitHubToken = "";
        Assert.Contains(o.Validate(), e => e.Contains("GitHubToken") && e.Contains("GitHubApp"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_missing_app_id_is_reported(long appId) =>
        Assert.Contains(AppAuth(a => a.AppId = appId).Validate(), e => e.Contains("GitHubApp.AppId"));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_missing_installation_id_is_reported(long installationId) =>
        Assert.Contains(
            AppAuth(a => a.InstallationId = installationId).Validate(),
            e => e.Contains("GitHubApp.InstallationId"));

    [Fact]
    public void A_github_app_block_with_no_private_key_at_all_is_reported() =>
        Assert.Contains(
            AppAuth(a => a.PrivateKey = "").Validate(),
            e => e.Contains("PrivateKey") && e.Contains("required"));

    [Fact]
    public void A_github_app_block_with_both_key_forms_is_reported()
    {
        var path = TempPem();
        try
        {
            Assert.Contains(
                AppAuth(a => a.PrivateKeyPath = path).Validate(),
                e => e.Contains("GitHubApp") && e.Contains("not both"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_private_key_path_that_exists_validates()
    {
        var path = TempPem();
        try
        {
            Assert.Empty(AppAuth(a => { a.PrivateKey = ""; a.PrivateKeyPath = path; }).Validate());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_private_key_path_that_does_not_exist_is_reported()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.pem");
        Assert.Contains(
            AppAuth(a => { a.PrivateKey = ""; a.PrivateKeyPath = missing; }).Validate(),
            e => e.Contains("PrivateKeyPath") && e.Contains("does not exist"));
    }

    [Fact]
    public void A_private_key_path_is_trimmed_on_assignment()
    {
        var path = TempPem();
        try
        {
            var o = AppAuth(a => { a.PrivateKey = ""; a.PrivateKeyPath = $" {path}\n"; });
            Assert.Equal(path, o.Apps[0].GitHubApp!.PrivateKeyPath);
            Assert.Empty(o.Validate());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Binds_a_github_app_block_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Discord:Token"] = "tok",
            ["OpenAI:ApiKey"] = "key",
            ["Apps:0:Name"] = "MyApp",
            ["Apps:0:Repo"] = "owner/repo",
            ["Apps:0:GitHubApp:AppId"] = "12345",
            ["Apps:0:GitHubApp:InstallationId"] = "987654",
            ["Apps:0:GitHubApp:PrivateKey"] = Pem,
            ["Apps:0:GuildIds:0"] = "111111111111111111",
            ["Apps:0:ChannelIds:0"] = "222222222222222222",
        }).Build();

        var o = config.Get<BotOptions>()!;

        Assert.Empty(o.Validate());
        Assert.Equal(12345L, o.Apps[0].GitHubApp!.AppId);
        Assert.Equal(987654L, o.Apps[0].GitHubApp!.InstallationId);
    }

    [Fact]
    public void A_pat_only_app_binds_without_a_github_app_block()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Apps:0:GitHubToken"] = "pat",
        }).Build();

        Assert.Null(config.Get<BotOptions>()!.Apps[0].GitHubApp);
    }

    private static string TempPem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"key-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, Pem);
        return path;
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
