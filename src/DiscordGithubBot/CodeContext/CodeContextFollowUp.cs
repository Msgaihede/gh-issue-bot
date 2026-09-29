using System.Diagnostics;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.CodeContext;

public interface ICodeContextFollowUp
{
    /// <summary>
    /// Waits in the background for a created issue's code context and edits it into the issue body; returns at
    /// once. The returned task completes when the edit is done or given up and never faults, so the caller need
    /// not await it.
    /// </summary>
    /// <param name="composeBody">the issue body as it would read with the given code context</param>
    Task Start(PendingReport report, AppConfig app, int issueNumber, Func<string, string> composeBody);
}

/// <summary>
/// Takes the code context off the "Create issue" wait when the reporter clicks before it is built: the issue is
/// created without it, and this edits it into the body once the build is done. An edit, never a comment — the
/// body alone is what a maintainer or an agent handed the issue reads. The edit re-reads the issue and replaces
/// only what follows its marker, so a change made to the report text in the meantime survives. Losing the edit (a
/// GitHub failure, a restart mid-build) leaves the issue without code context, which is what a failed build
/// produces anyway.
/// </summary>
public sealed class CodeContextFollowUp(
    ICodeContextPrefetcher prefetcher, IServiceScopeFactory scopes, ILogger<CodeContextFollowUp> logger)
    : ICodeContextFollowUp
{
    public Task Start(PendingReport report, AppConfig app, int issueNumber, Func<string, string> composeBody) =>
        Task.Run(() => EditInAsync(report, app, issueNumber, composeBody));

    private async Task EditInAsync(PendingReport report, AppConfig app, int issueNumber, Func<string, string> composeBody)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var codeContext = await prefetcher.GetAsync(report, app);
            if (codeContext is null)
            {
                logger.LogDebug("Code context for issue #{Number} in {Repo} found nothing to add; the issue stays as created.",
                    issueNumber, app.Repo);
                return;
            }

            // Its own scope: the click's scope is disposed as soon as the reporter has their answer.
            await using var scope = scopes.CreateAsyncScope();
            var gitHub = scope.ServiceProvider.GetRequiredService<IGitHubService>();

            var current = await gitHub.GetIssueAsync(app, issueNumber);
            var body = IssueBodyComposer.ReplaceBoilerplate(current.Body, composeBody(codeContext));
            if (body is null)
            {
                logger.LogWarning("Issue #{Number} in {Repo} no longer carries the bot's marker; its code context was not added.",
                    issueNumber, app.Repo);
                return;
            }

            await gitHub.UpdateIssueBodyAsync(app, issueNumber, body);
            logger.LogInformation("Added code context to issue #{Number} in {Repo} from report {ReportId}, {Seconds:0.0} s after creating it.",
                issueNumber, app.Repo, ReportPipeline.ShortId(report.Id), clock.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Adding code context to issue #{Number} in {Repo} failed; the issue goes without.",
                issueNumber, app.Repo);
        }
    }
}
