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
        // Capped on purpose: the downloader can only measure a body it has already buffered, and the
        // declared size gating the request comes from the uploading client, which is exactly the metadata
        // it must not trust. One byte above the limit, so a body of MaxBytes + 1 still reaches the
        // downloader's own length check and anything larger fails here into its catch as a skipped file.
        services.AddHttpClient<AttachmentDownloader>(
            c => c.MaxResponseContentBufferSize = AttachmentDownloader.MaxBytes + 1);

        // AI (OpenAIClient construction is lazy and network-free; startup validation guarantees a key,
        // and the DI test passes a dummy key)
        var openAi = new OpenAIClient(options.OpenAI.ApiKey);
        services.AddSingleton(openAi.GetChatClient(options.OpenAI.ChatModel).AsIChatClient());
        services.AddSingleton(openAi.GetEmbeddingClient(options.OpenAI.EmbeddingModel)
            .AsIEmbeddingGenerator(VectorRanker.EmbeddingDimensions));

        // pipeline
        services.AddScoped<IReportNormalizer, ReportNormalizer>();
        services.AddScoped<IDuplicateJudge, DuplicateJudge>();
        services.AddScoped<IAdditionalInfoExtractor, AdditionalInfoExtractor>();
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

    private static void ConfigureGitHubClient(HttpClient http)
    {
        http.BaseAddress = new Uri(GitHubApiBaseAddress);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("discord-gh-issue-bot");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
}
