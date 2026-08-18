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
using OpenAI;

namespace DiscordGithubBot;

/// <summary>
/// The one place that knows how every service in the bot is built. Program.cs and the DI test both go
/// through it, so a lifetime mistake shows up in the test suite rather than at three in the morning.
/// </summary>
public static class HostSetup
{
    /// <summary>Base address for both GitHub clients; the uploads host is addressed absolutely.</summary>
    private const string GitHubApiBaseAddress = "https://api.github.com/";

    public static IServiceCollection AddBotServices(this IServiceCollection services, BotOptions options)
    {
        services.AddSingleton(options);

        // database
        services.AddDbContext<BotDbContext>(o => o.UseSqlite($"Data Source={options.Database.Path}"));

        // GitHub over HttpClient
        services.AddHttpClient<IGitHubService, GitHubService>(ConfigureGitHubClient);
        services.AddHttpClient<IImageUploader, GitHubImageUploader>(ConfigureGitHubClient);
        services.AddHttpClient<AttachmentDownloader>();

        // AI (OpenAIClient construction is lazy and network-free; startup validation guarantees a key,
        // and the DI test passes a dummy key)
        var openAi = new OpenAIClient(options.OpenAI.ApiKey);
        services.AddSingleton(openAi.GetChatClient(options.OpenAI.ChatModel).AsIChatClient());
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

    private static void ConfigureGitHubClient(HttpClient http)
    {
        http.BaseAddress = new Uri(GitHubApiBaseAddress);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("discord-gh-issue-bot");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
}
