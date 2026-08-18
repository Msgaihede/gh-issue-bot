using DiscordGithubBot;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordGithubBot.Tests;

public class HostSetupTests
{
    [Fact]
    public void All_pipeline_services_resolve()
    {
        var options = new BotOptions
        {
            Discord = new() { Token = "t" }, OpenAI = new() { ApiKey = "k" },
            Database = new() { Path = Path.Combine(Path.GetTempPath(), $"di-test-{Guid.NewGuid():N}.db") },
            Apps = [new AppConfig { Name = "A", Repo = "o/r", GitHubToken = "p", GuildIds = [1UL], ChannelIds = [2UL] }],
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotServices(options);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReportPipeline>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IIssueSyncService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPendingReportStore>());
    }
}
