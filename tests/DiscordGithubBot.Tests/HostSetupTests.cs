using Discord.Interactions;
using Discord.WebSocket;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Discord;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordGithubBot.Tests;

public class HostSetupTests
{
    private static BotOptions Options() => new()
    {
        Discord = new() { Token = "t" }, OpenAI = new() { ApiKey = "k" },
        Database = new() { Path = Path.Combine(Path.GetTempPath(), $"di-test-{Guid.NewGuid():N}.db") },
        Apps = [new AppConfig { Name = "A", Repo = "o/r", GitHubToken = "p", GuildIds = [1UL], ChannelIds = [2UL] }],
    };

    /// <summary>
    /// The container the bot actually runs on, with both guards on: <c>ValidateScopes</c> catches a scoped
    /// service captured by a singleton, <c>ValidateOnBuild</c> catches a dependency nothing registers.
    /// </summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotServices(Options());
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public void All_pipeline_services_resolve()
    {
        using var provider = BuildProvider();

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReportPipeline>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IIssueSyncService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPendingReportStore>());
    }

    /// <summary>
    /// Module discovery is handed a scope's provider rather than the root one, because the module itself
    /// cannot be constructed from the root: it takes the scoped <see cref="IReportPipeline"/>. Discord.Net
    /// instantiates a module while building it (that is what <c>OnModuleBuilding</c> is for), so the root
    /// provider is the wrong thing to hand it even where today's version gets away with it.
    /// </summary>
    [Fact]
    public void The_interaction_module_can_only_be_constructed_inside_a_scope()
    {
        using var provider = BuildProvider();

        Assert.Throws<InvalidOperationException>(
            () => ActivatorUtilities.CreateInstance<ReportInteractionModule>(provider));

        using var scope = provider.CreateScope();
        Assert.NotNull(ActivatorUtilities.CreateInstance<ReportInteractionModule>(scope.ServiceProvider));
    }

    /// <summary>Discovery itself must survive the same validated container, from inside a scope.</summary>
    [Fact]
    public async Task Interaction_modules_are_discovered_from_a_scope()
    {
        using var provider = BuildProvider();
        using var client = provider.GetRequiredService<DiscordSocketClient>();
        var interactions = provider.GetRequiredService<InteractionService>();

        using var scope = provider.CreateScope();
        var modules = await interactions.AddModulesAsync(typeof(BotService).Assembly, scope.ServiceProvider);

        Assert.NotEmpty(modules);
        Assert.Equal(3, interactions.SlashCommands.Count);
    }
}
