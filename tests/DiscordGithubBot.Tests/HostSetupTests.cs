using Discord.Interactions;
using Discord.WebSocket;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Discord;
using DiscordGithubBot.GitHub;
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
    /// The auth provider caches installation tokens, so a second instance means a second token per
    /// interaction — and, at scale, GitHub's rate limit. Its own registration is what keeps it single;
    /// the typed clients around it stay transient on purpose.
    /// </summary>
    [Fact]
    public void The_github_auth_provider_is_a_singleton()
    {
        using var provider = BuildProvider();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IGitHubAuthProvider>(),
            second.ServiceProvider.GetRequiredService<IGitHubAuthProvider>());
    }

    /// <summary>Both GitHub clients now take the auth provider; neither resolves if it is unregistered.</summary>
    [Fact]
    public void Both_github_clients_resolve_with_their_auth_provider()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IGitHubService>());
        Assert.NotNull(provider.GetRequiredService<IImageUploader>());
    }

    /// <summary>
    /// The declared attachment size is the uploading client talking, so it must not be the only thing
    /// bounding a download: uncapped, a CDN object far larger than its declared size is materialized whole
    /// before <see cref="AttachmentDownloader"/> can measure and refuse it. The cap sits one byte above the
    /// limit so a body of exactly <c>MaxBytes + 1</c> still reaches that check.
    /// </summary>
    [Fact]
    public void The_attachment_client_caps_how_much_it_will_buffer()
    {
        using var provider = BuildProvider();

        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(AttachmentDownloader));

        Assert.Equal(AttachmentDownloader.MaxBytes + 1, client.MaxResponseContentBufferSize);
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
