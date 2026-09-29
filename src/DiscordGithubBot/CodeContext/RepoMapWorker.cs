using System.Diagnostics;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.OpenRouter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.CodeContext;

/// <summary>
/// Keeps every app's repository map in step with its default branch: a check at startup, then one every
/// <see cref="Interval"/>. Each check asks GitHub for the branch head (two calls when nothing changed) and
/// summarizes every file added or changed since the last check — by blob SHA, so an edit is never missed and an
/// unchanged file is never paid for twice — which makes the startup check the full first build. Checks run on
/// the thread pool, never on the host's startup path, and never overlap: a tick that finds the previous check
/// still running is skipped, and the next tick tries again. Each app gets its own DI scope, so its database
/// context and usage meter are its own, and a failure is a warning rather than a crash: the next check retries it.
/// </summary>
public sealed class RepoMapWorker(
    IServiceScopeFactory scopes, BotOptions options, TimeProvider time, ILogger<RepoMapWorker> logger) : BackgroundService
{
    /// <summary>
    /// How often a check starts. Short because a check that finds nothing changed costs two GitHub calls and no
    /// model calls, and every minute of lag is a minute in which new reports are matched against the previous
    /// version of the files that changed.
    /// </summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly NonOverlappingRunner _runner = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Checking the repository maps of {Count} app(s) now and every {Minutes} minutes; a check still running then delays the next one.",
            options.Apps.Count, Interval.TotalMinutes);

        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            do
            {
                if (!_runner.TryStart(() => CheckAllAsync(stoppingToken)))
                    logger.LogInformation(
                        "The previous repository map check is still running; skipping this one, next try in {Minutes} minutes.",
                        Interval.TotalMinutes);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }

        // A running check sees the same token; let it wind down rather than abandon it mid-save.
        try { await _runner.Current; }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var app in options.Apps) await UpdateAsync(app, ct);
    }

    private async Task UpdateAsync(AppConfig app, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var started = Stopwatch.GetTimestamp();
            var result = await scope.ServiceProvider.GetRequiredService<IRepoMapService>().UpdateAsync(app, ct);

            if (result.Summarized > 0 || result.Removed > 0 || result.Embedded > 0 || !result.Complete)
            {
                var usage = scope.ServiceProvider.GetRequiredService<AiUsageMeter>();
                logger.LogInformation(
                    "Repository map of {Repo} checked in {Elapsed}: {Summarized} file(s) summarized, {Embedded} embedded, {Removed} removed, {State}; AI usage {Usage}.",
                    app.Repo, FormatElapsed(Stopwatch.GetElapsedTime(started)), result.Summarized, result.Embedded, result.Removed,
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

    /// <summary>"42 s" or "12 min 5 s": a first build runs for minutes, an incremental one for seconds.</summary>
    internal static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds < 60
        ? $"{elapsed.TotalSeconds:0} s"
        : $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s";
}

/// <summary>
/// Starts work on the thread pool unless the previous run is still going. Only the worker's own loop calls it,
/// so there is no race between the check and the start.
/// </summary>
internal sealed class NonOverlappingRunner
{
    public Task Current { get; private set; } = Task.CompletedTask;

    /// <returns>false, starting nothing, while the previous run has not finished.</returns>
    public bool TryStart(Func<Task> work)
    {
        if (!Current.IsCompleted) return false;
        Current = Task.Run(work);
        return true;
    }
}
