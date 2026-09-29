using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot;

/// <summary>
/// What this instance is about to run with, written once at startup: which apps it serves, how each one
/// authenticates, which models it calls and where its database lives. It answers most "why did the bot do
/// that" questions before anyone has to read the configuration. Never logs a secret — only whether one is set.
/// </summary>
public static class StartupLog
{
    public static void Write(ILogger logger, BotOptions options)
    {
        var o = options.OpenRouter;
        logger.LogInformation(
            "Models: chat {ChatModel} (reasoning {Effort}), decisions {DecisionModel}, embeddings {EmbeddingModel}; chat providers [{Providers}], retries on [{RetryProviders}].",
            o.ChatModel, string.IsNullOrWhiteSpace(o.ReasoningEffort) ? "model default" : o.ReasoningEffort.Trim(),
            o.DecisionModel, o.EmbeddingModel,
            string.Join(", ", o.EffectiveChatProviders), string.Join(", ", o.EffectiveRegularProviders));

        logger.LogInformation(
            "Database {Path} (schema v{Version}); each user may file {Limit} report(s) a day.",
            Path.GetFullPath(options.Database.Path), DatabaseSchema.Version, options.Limits.ReportsPerUserPerDay);

        logger.LogInformation("Serving {Count} app(s).", options.Apps.Count);
        foreach (var app in options.Apps)
            logger.LogInformation(
                "App {Name} -> {Repo}: GitHub {Auth}, {Guilds} server(s) [{GuildIds}], announcing in {Channels} channel(s).",
                app.Name, app.Repo, app.GitHubApp is null ? "PAT" : $"App {app.GitHubApp.AppId} (installation {app.GitHubApp.InstallationId})",
                app.GuildIds.Count, string.Join(", ", app.GuildIds), app.ChannelIds.Count);
    }
}
