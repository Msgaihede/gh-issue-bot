using System.Collections.Concurrent;
using System.Diagnostics;
using DiscordGithubBot.Ai;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.CodeContext;

public interface ICodeContextPrefetcher
{
    /// <summary>Starts building a drafted report's code context in the background; returns at once.</summary>
    void Start(Guid pendingReportId, AppConfig app, IssueDraft draft);

    /// <summary>
    /// The code context for a report being created: the stored result when the background build finished,
    /// the build itself when it is still running, and a build started now when there is neither (the bot
    /// restarted in between). Null when there is nothing worth adding. Never throws on a model failure.
    /// </summary>
    Task<string?> GetAsync(PendingReport report, AppConfig app, CancellationToken ct = default);
}

/// <summary>
/// Takes the code context off both waits a reporter sees. It is not shown in the preview — it is for
/// maintainers, and a Discord reporter need not see a repository's code — so it need not hold the preview
/// up; and it is built while the reporter reads the preview, so it is usually done before they press
/// "Create issue". Each build runs in its own DI scope (the interaction's scope is gone by then) and saves its
/// result on the pending report, which is what survives a restart; the in-memory task only saves a
/// confirmation that arrives mid-build from building twice.
/// </summary>
public sealed class CodeContextPrefetcher(IServiceScopeFactory scopes, ILogger<CodeContextPrefetcher> logger)
    : ICodeContextPrefetcher
{
    /// <summary>Pending reports live an hour; a build nobody asked for by then never will be.</summary>
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, (Task<string?> Build, DateTime StartedUtc)> _builds = new();

    public void Start(Guid pendingReportId, AppConfig app, IssueDraft draft)
    {
        Prune();
        _builds[pendingReportId] = (Task.Run(() => BuildAndStoreAsync(pendingReportId, app, draft)), DateTime.UtcNow);
    }

    public async Task<string?> GetAsync(PendingReport report, AppConfig app, CancellationToken ct = default)
    {
        if (report.CodeContextReadyAtUtc is not null) return EmptyAsNull(report.CodeContext);

        if (_builds.TryGetValue(report.Id, out var running)) return await running.Build.WaitAsync(ct);

        // Started before a restart, or never started: the reporter waits for this one build.
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICodeContextBuilder>()
            .BuildAsync(app, new IssueDraft(report.DraftTitle, report.DraftBody), ct);
    }

    private async Task<string?> BuildAndStoreAsync(Guid pendingReportId, AppConfig app, IssueDraft draft)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;

            var markdown = await services.GetRequiredService<ICodeContextBuilder>().BuildAsync(app, draft);
            // "" records "built, nothing worth adding", so a confirmation after a restart does not build again.
            await services.GetRequiredService<IPendingReportStore>().SetCodeContextAsync(pendingReportId, markdown ?? "");

            logger.LogInformation("Code context for report {ReportId} in {Repo} ready in {Seconds:0.0} s ({Outcome}); AI usage {Usage}.",
                Pipeline.ReportPipeline.ShortId(pendingReportId), app.Repo, clock.Elapsed.TotalSeconds, markdown is null ? "nothing to add" : "added",
                services.GetRequiredService<AiUsageMeter>());
            return markdown;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Building code context in the background for report {ReportId} in {Repo} failed; the issue goes without.",
                Pipeline.ReportPipeline.ShortId(pendingReportId), app.Repo);
            return null;
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - KeepFor;
        foreach (var (id, build) in _builds)
            if (build.StartedUtc < cutoff && build.Build.IsCompleted) _builds.TryRemove(id, out _);
    }

    private static string? EmptyAsNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
