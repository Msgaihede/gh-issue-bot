using DiscordGithubBot.Configuration;
using DiscordGithubBot.OpenRouter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.CodeContext;

/// <summary>
/// Keeps every app's repository map in step with its default branch. A pass checks each branch head (two
/// GitHub calls when nothing changed) and summarizes what moved; it runs hourly, and every few minutes
/// while some map is still catching up — a first build is capped per pass, so a large repository takes a
/// few passes. Each app gets its own DI scope, so its database context and usage meter are its own, and a
/// failure is a warning rather than a crash.
/// </summary>
public sealed class RepoMapWorker(IServiceScopeFactory scopes, BotOptions options, ILogger<RepoMapWorker> logger)
    : BackgroundService
{
    /// <summary>Lets the gateway connect and the first reports through before the map starts spending.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan CatchUpInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            while (true)
            {
                var catchingUp = false;
                foreach (var app in options.Apps) catchingUp |= !await RefreshAsync(app, stoppingToken);

                await Task.Delay(catchingUp ? CatchUpInterval : Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
    }

    /// <returns>whether the app's map is complete.</returns>
    private async Task<bool> RefreshAsync(AppConfig app, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IRepoMapService>().RefreshAsync(app, ct);

            if (result.Summarized > 0 || result.Removed > 0)
            {
                var usage = scope.ServiceProvider.GetRequiredService<AiUsageMeter>();
                logger.LogInformation(
                    "Repository map of {Repo}: {Summarized} file(s) summarized, {Removed} removed, {State}; AI cost ${Cost}.",
                    app.Repo, result.Summarized, result.Removed, result.Complete ? "complete" : "catching up", usage.TotalCost);
            }

            return result.Complete;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The repository map pass for {Repo} failed.", app.Repo);
            return false;
        }
    }
}
