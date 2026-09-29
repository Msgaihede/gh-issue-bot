using DiscordGithubBot.CodeContext;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Tests;

public sealed class LoggingTests
{
    /// <summary>The shipped appsettings.json, read from the source tree so the test sees what Docker ships.</summary>
    private static IConfiguration ShippedSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "DiscordGithubBot.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);

        return new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(dir.FullName, "src", "DiscordGithubBot", "appsettings.json"))
            .Build();
    }

    /// <summary>
    /// The HTTP client factory writes four lines per request at Information, which buried every line the bot
    /// itself wrote; its warnings still come through. The bot's own Information lines and the host's
    /// start/stop lines stay.
    /// </summary>
    [Theory]
    [InlineData("System.Net.Http.HttpClient.IGitHubService.LogicalHandler", LogLevel.Information, false)]
    [InlineData("System.Net.Http.HttpClient.IOpenRouterChat.ClientHandler", LogLevel.Information, false)]
    [InlineData("System.Net.Http.HttpClient.IGitHubService.LogicalHandler", LogLevel.Warning, true)]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Information, false)]
    [InlineData("Microsoft.Hosting.Lifetime", LogLevel.Information, true)]
    [InlineData("DiscordGithubBot.CodeContext.RepoMapWorker", LogLevel.Information, true)]
    [InlineData("DiscordGithubBot.OpenRouter.OpenRouterChatClient", LogLevel.Debug, false)]
    public void Library_chatter_is_quiet_and_the_bots_own_lines_are_not(string category, LogLevel level, bool enabled)
    {
        using var factory = LoggerFactory.Create(b => b
            .AddConfiguration(ShippedSettings().GetSection("Logging"))
            .AddProvider(new RecordingProvider()));

        Assert.Equal(enabled, factory.CreateLogger(category).IsEnabled(level));
    }

    [Fact]
    public void The_console_writes_one_line_per_entry()
    {
        Assert.Equal("True", ShippedSettings()["Logging:Console:FormatterOptions:SingleLine"], ignoreCase: true);
    }

    [Fact]
    public void The_startup_summary_names_every_app_and_model_but_no_secret()
    {
        var options = new BotOptions
        {
            OpenRouter = { ApiKey = "sk-secret" },
            Apps =
            [
                new AppConfig { Name = "Grimoire", Repo = "Owner/Grimoire", GitHubToken = "ghp_secret", GuildIds = [42] },
                new AppConfig
                {
                    Name = "Other", Repo = "Owner/Other",
                    GitHubApp = new GitHubAppAuth { AppId = 7, InstallationId = 9, PrivateKey = "-----BEGIN secret" },
                },
            ],
        };
        var logger = new ListLogger<LoggingTests>();

        StartupLog.Write(logger, options);

        var text = string.Join("\n", logger.Messages(LogLevel.Information));
        Assert.Contains("openai/gpt-6-luna (reasoning medium)", text);
        Assert.Contains("typesafe/jev-1.13", text);
        Assert.Contains("App Grimoire -> Owner/Grimoire: GitHub PAT, 1 server(s) [42]", text);
        Assert.Contains("App Other -> Owner/Other: GitHub App 7 (installation 9)", text);
        Assert.DoesNotContain("secret", text);
    }

    [Theory]
    [InlineData(4.4, "4 s")]
    [InlineData(59, "59 s")]
    [InlineData(725, "12 min 5 s")]
    public void Check_durations_read_in_minutes_once_they_pass_one(double seconds, string expected) =>
        Assert.Equal(expected, RepoMapWorker.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    private sealed class RecordingProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger<RecordingProvider>();
        public void Dispose() { }
    }
}
