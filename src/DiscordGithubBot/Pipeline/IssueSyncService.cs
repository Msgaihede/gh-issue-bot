using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Pipeline;

public interface IIssueSyncService
{
    /// <summary>Refreshes the cached open issues of the app's repo. Never throws on a GitHub failure — logs and leaves the cache stale.</summary>
    Task SyncAsync(AppConfig app, CancellationToken ct = default);

    /// <summary>The repo's open issues as last synced, oldest first.</summary>
    Task<IReadOnlyList<CachedIssue>> GetOpenIssuesAsync(string repoKey, CancellationToken ct = default);
}

/// <summary>
/// Keeps a local copy of a repository's open issues — number, title, link and the start of the body — for
/// the duplicate finder to read. Two kinds of pass keep it true. A <em>full</em> pass lists every open
/// issue and replaces the cache with exactly that, which also clears out issues that vanished in ways an
/// update feed never reports (deleted, transferred). An <em>incremental</em> pass asks GitHub for anything
/// updated since the last sync, open or closed: open ones are upserted, closed ones deleted. A full pass
/// runs first and then once a day; everything in between is incremental and usually a single call.
/// </summary>
public sealed class IssueSyncService(
    BotDbContext db, IGitHubService gitHub, ILogger<IssueSyncService> logger) : IIssueSyncService
{
    /// <summary>How long incremental passes may run before a full pass re-anchors the cache.</summary>
    private static readonly TimeSpan FullResyncInterval = TimeSpan.FromDays(1);

    /// <summary>Characters of body kept: enough for the duplicate finder's side-by-side read.</summary>
    public const int BodyExcerptLength = 2500;

    public async Task SyncAsync(AppConfig app, CancellationToken ct = default)
    {
        var repoKey = NormalizeRepoKey(app.Repo);

        // Captured before the GitHub call: anything updated while we are syncing must fall inside the next
        // sync's window, even if that means fetching it twice.
        var syncStartUtc = DateTime.UtcNow;

        try
        {
            var state = await db.RepoSyncStates.FindAsync([repoKey], ct);
            var full = state is null || syncStartUtc - state.LastFullSyncUtc >= FullResyncInterval;

            var issues = full
                ? await gitHub.ListIssuesAsync(app, "open", null, ct)
                : await gitHub.ListIssuesAsync(app, "all", state!.LastSyncUtc, ct);

            var rows = await db.CachedIssues.Where(e => e.RepoKey == repoKey).ToDictionaryAsync(e => e.IssueNumber, ct);

            foreach (var issue in issues)
            {
                if (IsOpen(issue)) Upsert(repoKey, issue, rows);
                else if (rows.Remove(issue.Number, out var closed)) db.CachedIssues.Remove(closed);
            }

            // A full listing is the whole truth: whatever it did not mention is no longer open.
            if (full)
            {
                var listed = issues.Select(i => i.Number).ToHashSet();
                db.CachedIssues.RemoveRange(rows.Values.Where(r => !listed.Contains(r.IssueNumber)));
            }

            if (state is null)
            {
                db.RepoSyncStates.Add(new RepoSyncState
                {
                    RepoKey = repoKey, LastSyncUtc = syncStartUtc, LastFullSyncUtc = syncStartUtc,
                });
            }
            else
            {
                state.LastSyncUtc = syncStartUtc;
                if (full) state.LastFullSyncUtc = syncStartUtc;
            }

            // One save, watermark included: a pass that fails half way leaves the watermark where it was,
            // so the next pass repeats the window instead of skipping it.
            await db.SaveChangesAsync(ct);
            logger.LogDebug("Synced {Count} issue(s) for {Repo} ({Kind} pass).", issues.Count, repoKey, full ? "full" : "incremental");
        }
        // Only a genuine cancellation of *our* token escapes: an HttpClient timeout also surfaces as a
        // TaskCanceledException, and that is an ordinary GitHub failure the cache should absorb.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            RollbackPendingChanges();
            throw;
        }
        catch (Exception ex)
        {
            // A stale cache still finds duplicates; a thrown sync would kill the whole report.
            logger.LogWarning(ex, "Issue sync for {Repo} failed; continuing with the cached issues.", repoKey);
            RollbackPendingChanges();
        }
    }

    public async Task<IReadOnlyList<CachedIssue>> GetOpenIssuesAsync(string repoKey, CancellationToken ct = default)
    {
        var key = NormalizeRepoKey(repoKey);
        return await db.CachedIssues.AsNoTracking()
            .Where(e => e.RepoKey == key)
            .OrderBy(e => e.IssueNumber)
            .ToListAsync(ct);
    }

    /// <summary>
    /// The part of an issue body that says what the issue is about. Bodies this bot composed end with
    /// generated material — the attribution footer, screenshots, upload notes, the code-context block —
    /// which is near-identical across every issue it files and would make them all read alike to the
    /// duplicate finder. Everything from <see cref="IssueBodyComposer.MetaMarker"/> on is dropped; a body
    /// without the marker (every human-authored issue) is used whole.
    /// </summary>
    public static string SemanticBody(string body)
    {
        var marker = body.IndexOf(IssueBodyComposer.MetaMarker, StringComparison.Ordinal);
        return marker < 0 ? body : body[..marker].Trim();
    }

    private static bool IsOpen(GitHubIssue issue) =>
        string.Equals(issue.State, "open", StringComparison.OrdinalIgnoreCase);

    private void Upsert(string repoKey, GitHubIssue issue, Dictionary<int, CachedIssue> rows)
    {
        if (!rows.TryGetValue(issue.Number, out var row))
        {
            row = new CachedIssue { RepoKey = repoKey, IssueNumber = issue.Number, Title = issue.Title };
            db.CachedIssues.Add(row);
            rows[issue.Number] = row;
        }

        row.Title = issue.Title;
        row.UpdatedAtUtc = issue.UpdatedAtUtc;
        row.HtmlUrl = issue.HtmlUrl;
        row.BodyExcerpt = Truncate(SemanticBody(issue.Body), BodyExcerptLength);
    }

    /// <summary>
    /// Drops this sync's unsaved edits. The <see cref="BotDbContext"/> is shared with the rest of the
    /// operation, so half-written rows left tracked would resurface in the caller's next <c>SaveChanges</c>.
    /// </summary>
    private void RollbackPendingChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is not (CachedIssue or RepoSyncState)) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified or EntityState.Deleted:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
            }
        }
    }

    /// <summary>Repo keys are stored lowercase so lookups never depend on how the repo was configured.</summary>
    private static string NormalizeRepoKey(string repo) => repo.ToLowerInvariant();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
