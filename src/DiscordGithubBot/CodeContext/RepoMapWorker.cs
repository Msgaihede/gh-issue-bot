using DiscordGithubBot.Configuration;
using DiscordGithubBot.OpenRouter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.CodeContext;

/// <summary>
/// Keeps every app's repository map in step with its default branch: once at startup, then every
/// <see cref="Interval"/>. Each check asks GitHub for the branch head (two calls when nothing changed) and
/// summarizes every file added or changed since the last check — by blob SHA, so an edit is never missed —
/// running until the map is complete, which makes the startup check the full first build. Each app gets its
/// own DI scope, so its database context and usage meter are its own, and a failure is a warning rather
/// than a crash: the next check retries it.
/// </summary>
public sealed class RepoMapWorker(IServiceScopeFactory scopes, BotOptions options, ILogger<RepoMapWorker> logger)
    : BackgroundService
{
    /// <summary>
    /// How long after one check ends the next begins. Short because a check that finds nothing changed costs
    /// two GitHub calls and no model calls, and every minute of lag is a minute in which new reports are
    /// matched against the previous version of the files that changed.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Yield first, so a long first build can never hold up the start of the other hosted services.
            await Task.Yield();

            while (true)
            {
                foreach (var app in options.Apps) await UpdateAsync(app, stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
    }

    private async Task UpdateAsync(AppConfig app, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<IRepoMapService>().UpdateAsync(app, ct);

            if (result.Summarized > 0 || result.Removed > 0 || !result.Complete)
            {
                var usage = scope.ServiceProvider.GetRequiredService<AiUsageMeter>();
                logger.LogInformation(
                    "Repository map of {Repo}: {Summarized} file(s) summarized, {Removed} removed, {State}; AI usage {Usage}.",
                    app.Repo, result.Summarized, result.Removed,
                    result.Complete ? "up to date" : "incomplete, retrying at the next check", usage);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The repository map update for {Repo} failed; retrying at the next check.", app.Repo);
        }
    }
}
