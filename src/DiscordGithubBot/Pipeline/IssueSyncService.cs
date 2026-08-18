using System.Security.Cryptography;
using System.Text;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Pipeline;

public interface IIssueSyncService
{
    /// <summary>Incrementally refreshes the embedding cache for the app's repo. Never throws on GitHub/embedding failure — logs and leaves the cache stale.</summary>
    Task SyncAsync(AppConfig app, CancellationToken ct = default);

    /// <summary>Candidates for dedup: open issues plus issues closed within the last 30 days. Prunes older closed rows.</summary>
    Task<IReadOnlyList<IssueEmbedding>> GetCandidatesAsync(string repoKey, CancellationToken ct = default);
}

/// <summary>
/// Keeps the cached issue embeddings for a repository in step with GitHub. Sync is incremental
/// (GitHub's <c>since</c> filter) and re-embeds only issues whose title or body actually changed,
/// so a routine sync costs one GitHub call and no embedding calls.
/// </summary>
public sealed class IssueSyncService(
    BotDbContext db, IGitHubService gitHub,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    ILogger<IssueSyncService> logger,
    int saveBatchSize = IssueSyncService.DefaultSaveBatchSize) : IIssueSyncService
{
    /// <summary>
    /// How many upserted issues are flushed at a time. A first sync of a busy repository is hundreds of
    /// embedding calls long, and without an intermediate flush a rate limit near the end would throw away
    /// every vector bought so far. The batch is only ever a save, never the watermark.
    /// </summary>
    public const int DefaultSaveBatchSize = 25;

    /// <summary>How long a closed issue stays a dedup candidate before it is pruned.</summary>
    private const int CandidateWindowDays = 30;

    /// <summary>Characters of the issue body kept for prompts and previews.</summary>
    private const int BodyExcerptLength = 1000;

    /// <summary>Characters of title + body sent to the embedding model.</summary>
    private const int EmbedTextLength = 8000;

    public async Task SyncAsync(AppConfig app, CancellationToken ct = default)
    {
        var repoKey = NormalizeRepoKey(app.Repo);

        // Captured before the GitHub call: anything updated while we are syncing must fall inside
        // the next sync's window, even if that means fetching it twice.
        var syncStartUtc = DateTime.UtcNow;

        try
        {
            var state = await db.RepoSyncStates.FindAsync([repoKey], ct);
            var issues = await gitHub.ListIssuesAsync(app, "all", state?.LastSyncUtc, ct);

            var numbers = issues.Select(i => i.Number).ToList();
            var rows = await db.IssueEmbeddings
                .Where(e => e.RepoKey == repoKey && numbers.Contains(e.IssueNumber))
                .ToDictionaryAsync(e => e.IssueNumber, ct);

            var sinceLastSave = 0;
            foreach (var issue in issues)
            {
                await UpsertAsync(repoKey, issue, rows, ct);

                // Flushed mid-loop so a failure part-way through keeps the issues already embedded;
                // RepoSyncState is untouched here, so no partial pass can advance the watermark.
                if (++sinceLastSave < saveBatchSize) continue;

                await db.SaveChangesAsync(ct);
                sinceLastSave = 0;
            }

            if (state is null)
                db.RepoSyncStates.Add(new RepoSyncState { RepoKey = repoKey, LastSyncUtc = syncStartUtc });
            else
                state.LastSyncUtc = syncStartUtc;

            // The watermark moves only here, once every issue in the window is embedded and stored:
            // a sync that failed half way must be repeated in full, not skipped as already done.
            await db.SaveChangesAsync(ct);
            logger.LogDebug("Synced {Count} issue(s) for {Repo}.", issues.Count, repoKey);
        }
        // Only a genuine cancellation of *our* token escapes: an HttpClient timeout also surfaces as
        // a TaskCanceledException, and that is an ordinary GitHub failure the cache should absorb.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            RollbackPendingChanges();
            throw;
        }
        catch (Exception ex)
        {
            // A stale cache still produces useful duplicate candidates; a thrown sync would kill the
            // whole report flow. The watermark is left untouched so the next sync retries this window.
            logger.LogWarning(ex, "Issue sync for {Repo} failed; continuing with the cached embeddings.", repoKey);
            RollbackPendingChanges();
        }
    }

    public async Task<IReadOnlyList<IssueEmbedding>> GetCandidatesAsync(
        string repoKey, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-CandidateWindowDays);
        await db.IssueEmbeddings
            .Where(e => e.State == "closed" && e.ClosedAtUtc != null && e.ClosedAtUtc < cutoff)
            .ExecuteDeleteAsync(ct);

        var key = NormalizeRepoKey(repoKey);
        return await db.IssueEmbeddings.AsNoTracking()
            .Where(e => e.RepoKey == key)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Drops this sync's unsaved edits. The <see cref="BotDbContext"/> is shared with the rest of the
    /// operation, so half-written rows left tracked would resurface in the caller's next
    /// <c>SaveChanges</c> — and a second sync would then add a duplicate of every unsaved row and
    /// break the unique (RepoKey, IssueNumber) index, turning one failed sync into a dead context.
    /// </summary>
    private void RollbackPendingChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is not (IssueEmbedding or RepoSyncState)) continue;

            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
            }
        }
    }

    private async Task UpsertAsync(
        string repoKey, GitHubIssue issue, Dictionary<int, IssueEmbedding> rows, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issue.Title + "\n" + issue.Body)));

        if (!rows.TryGetValue(issue.Number, out var row))
        {
            row = new IssueEmbedding
            {
                RepoKey = repoKey, IssueNumber = issue.Number,
                Title = issue.Title, State = issue.State, ContentHash = "",
            };
            db.IssueEmbeddings.Add(row);
            rows[issue.Number] = row;
            await EmbedAsync(row, issue, hash, ct);
        }
        else if (row.ContentHash != hash)
        {
            await EmbedAsync(row, issue, hash, ct);
        }

        // Metadata is refreshed on every sync even when the content hash is unchanged: an issue can
        // be closed or renamed-back without its embedded text differing from what we cached.
        row.Title = issue.Title;
        row.State = issue.State;
        row.ClosedAtUtc = issue.ClosedAtUtc;
        row.UpdatedAtUtc = issue.UpdatedAtUtc;
        row.HtmlUrl = issue.HtmlUrl;
        row.BodyExcerpt = Truncate(issue.Body, BodyExcerptLength);
    }

    private async Task EmbedAsync(IssueEmbedding row, GitHubIssue issue, string hash, CancellationToken ct)
    {
        var text = Truncate(issue.Title + "\n\n" + issue.Body, EmbedTextLength);
        var vector = await embedder.GenerateVectorAsync(text, cancellationToken: ct);
        row.Vector = vector.ToArray();

        // The hash advances only once the vector it describes is in hand, so a failed embedding
        // leaves the row looking stale and the next sync retries it instead of skipping it forever.
        row.ContentHash = hash;
    }

    /// <summary>Repo keys are stored lowercase so lookups never depend on how the repo was configured.</summary>
    private static string NormalizeRepoKey(string repo) => repo.ToLowerInvariant();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
