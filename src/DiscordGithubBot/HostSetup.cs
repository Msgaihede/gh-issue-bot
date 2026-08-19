using System.ClientModel;
using DiscordGithubBot.Ai;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.Discord;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using global::Discord;
using global::Discord.Interactions;
using global::Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;

// ServiceTier is [Experimental] in the OpenAI SDK; running chat at the flex tier is the reason we accept that.
#pragma warning disable OPENAI001

namespace DiscordGithubBot;

/// <summary>
/// The one place that knows how every service in the bot is built. Program.cs and the DI test both go
/// through it, so a lifetime mistake shows up in the test suite rather than at three in the morning.
/// </summary>
public static class HostSetup
{
    /// <summary>Base address for every GitHub client; the uploads host is addressed absolutely.</summary>
    private const string GitHubApiBaseAddress = "https://api.github.com/";

    /// <summary>Named client for the token exchange — named rather than typed, see the registration below.</summary>
    private const string GitHubAuthClientName = "github-auth";

    /// <summary>
    /// How long a pooled connection on the long-lived auth client may live. Two minutes is the default
    /// handler lifetime this replaces, so DNS changes are picked up on the same schedule as elsewhere.
    /// </summary>
    private static readonly TimeSpan AuthConnectionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Network timeout for every OpenAI call. Flex-tier chat requests queue on spare capacity, so the
    /// client default of 100 seconds would turn ordinary queuing into failures; five minutes rides out
    /// the queue while keeping the normalizer's two attempts plus the judge inside the 15 minutes a
    /// deferred Discord interaction token lives. Embeddings answer in seconds on any tier — for them
    /// this widens only the failure cap, not the latency.
    /// </summary>
    private static readonly TimeSpan OpenAiNetworkTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Headers whose values <c>IHttpClientFactory</c>'s own logging must replace with <c>*</c>. It logs
    /// request and response headers at Trace, which on a GitHub client means a PAT — or a freshly minted
    /// installation token — in plain text in whatever collects the logs. Applied to every GitHub-facing
    /// client, including the token exchange, whose request carries the App JWT and whose response carries
    /// the installation token.
    /// </summary>
    private static readonly string[] RedactedHeaders = ["Authorization"];

    public static IServiceCollection AddBotServices(this IServiceCollection services, BotOptions options)
    {
        services.AddSingleton(options);

        // database
        services.AddDbContext<BotDbContext>(o => o.UseSqlite($"Data Source={options.Database.Path}"));

        // GitHub over HttpClient. The auth provider is the one GitHub client that must be a singleton —
        // it caches installation tokens, and a transient would mint a fresh one per interaction. A typed
        // client is transient by construction and would capture a factory handler forever, so it gets a
        // *named* client instead, with the documented singleton mitigation: connection recycling moves
        // down to SocketsHttpHandler, and the factory's own handler rotation is switched off.
        services.AddHttpClient(GitHubAuthClientName, ConfigureGitHubClient)
            .RedactLoggedHeaders(RedactedHeaders)
            .UseSocketsHttpHandler((handler, _) => handler.PooledConnectionLifetime = AuthConnectionLifetime)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddSingleton<IGitHubAuthProvider>(sp => new GitHubAuthProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubAuthClientName),
            sp.GetRequiredService<ILogger<GitHubAuthProvider>>()));

        services.AddHttpClient<IGitHubService, GitHubService>(ConfigureGitHubClient)
            .RedactLoggedHeaders(RedactedHeaders);
        services.AddHttpClient<IImageUploader, GitHubImageUploader>(ConfigureGitHubClient)
            .RedactLoggedHeaders(RedactedHeaders);
        services.AddHttpClient<AttachmentDownloader>();

        // AI (OpenAIClient construction is lazy and network-free; startup validation guarantees a key,
        // and the DI test passes a dummy key)
        var openAi = new OpenAIClient(
            new ApiKeyCredential(options.OpenAI.ApiKey),
            new OpenAIClientOptions { NetworkTimeout = OpenAiNetworkTimeout });
        services.AddSingleton(openAi.GetChatClient(options.OpenAI.ChatModel).AsIChatClient()
            .AsBuilder()
            .ConfigureOptions(o => ApplyServiceTier(o, options.OpenAI.ServiceTier))
            .Build());
        services.AddSingleton(openAi.GetEmbeddingClient(options.OpenAI.EmbeddingModel)
            .AsIEmbeddingGenerator(VectorRanker.EmbeddingDimensions));

        // pipeline
        services.AddScoped<IReportNormalizer, ReportNormalizer>();
        services.AddScoped<IDuplicateJudge, DuplicateJudge>();
        services.AddScoped<IIssueSyncService, IssueSyncService>();
        services.AddScoped<IPendingReportStore, PendingReportStore>();
        services.AddScoped<IReportPipeline, ReportPipeline>();

        // discord
        services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
        }));
        services.AddSingleton(sp => new InteractionService(
            sp.GetRequiredService<DiscordSocketClient>(), BotService.CreateConfig()));
        services.AddHostedService<BotService>();
        services.AddHostedService<MaintenanceService>();
        return services;
    }

    /// <summary>
    /// Routes a chat call at the configured OpenAI service tier by seeding the request's provider-native
    /// options; the adapter layers the strongly-typed <see cref="ChatOptions"/> on top of the seed. The
    /// factory builds a fresh instance per call because the adapter mutates the seed with the rest of the
    /// request — a shared one would leak one call's state into the next.
    /// </summary>
    public static void ApplyServiceTier(ChatOptions chatOptions, string configuredTier) =>
        chatOptions.RawRepresentationFactory = _ => new ChatCompletionOptions
        {
            ServiceTier = new ChatServiceTier(configuredTier.ToLowerInvariant()),
        };

    private static void ConfigureGitHubClient(HttpClient http)
    {
        http.BaseAddress = new Uri(GitHubApiBaseAddress);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("discord-gh-issue-bot");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
}
