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
    /// <summary>Incrementally refreshes the embedding cache for the app's repo, and re-embeds any rows left behind by an earlier embedding model. Never throws on GitHub/embedding failure — logs and leaves the cache stale.</summary>
    Task SyncAsync(AppConfig app, CancellationToken ct = default);

    /// <summary>Candidates for dedup: open issues plus issues closed within the last 30 days, embedded by the configured model. Prunes older closed rows.</summary>
    Task<IReadOnlyList<IssueEmbedding>> GetCandidatesAsync(string repoKey, CancellationToken ct = default);
}

/// <summary>
/// Keeps the cached issue embeddings for a repository in step with GitHub. Sync is incremental
/// (GitHub's <c>since</c> filter) and re-embeds only issues whose title or body actually changed —
/// or whose vector came from a model that is no longer the configured one — so a routine sync costs
/// one GitHub call and no embedding calls. "Body" here means the reporter's half of it — see
/// <see cref="SemanticBody"/>.
/// </summary>
public sealed class IssueSyncService(
    BotDbContext db, IGitHubService gitHub,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    BotOptions options,
    ILogger<IssueSyncService> logger,
    int saveBatchSize = IssueSyncService.DefaultSaveBatchSize) : IIssueSyncService
{
    /// <summary>
    /// The model every usable vector in the cache came from. Read once: the configured value cannot
    /// change without a restart, and a mid-sync change would leave half a pass stamped either way.
    /// </summary>
    private readonly string _embeddingModel = options.OpenAI.EmbeddingModel;

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

            // Flushed before the heal pass so its query sees the rows this loop just stamped; on a
            // stale read they would look mismatched and be paid for a second time.
            await db.SaveChangesAsync(ct);
            var healed = await ReembedMismatchedModelsAsync(repoKey, ct);

            if (state is null)
                db.RepoSyncStates.Add(new RepoSyncState { RepoKey = repoKey, LastSyncUtc = syncStartUtc });
            else
                state.LastSyncUtc = syncStartUtc;

            // The watermark moves only here, once every issue in the window is embedded and stored:
            // a sync that failed half way must be repeated in full, not skipped as already done.
            await db.SaveChangesAsync(ct);
            logger.LogDebug(
                "Synced {Count} issue(s) for {Repo}; re-embedded {Healed} row(s) left by an earlier model.",
                issues.Count, repoKey, healed);
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

        // Rows carrying another model's vector are left out rather than ranked: cosine similarity
        // between two models' embeddings is a number without a meaning, and a silently wrong ranking
        // is worse than a short candidate list. The next sync re-embeds them back into the list.
        var key = NormalizeRepoKey(repoKey);
        return await db.IssueEmbeddings.AsNoTracking()
            .Where(e => e.RepoKey == key && e.EmbeddingModel == _embeddingModel)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Re-embeds this repo's rows whose vectors came from a different model, and returns how many.
    /// Sync only fetches issues updated since the watermark, so switching the configured model would
    /// otherwise leave every untouched issue out of <see cref="GetCandidatesAsync"/> forever — the
    /// cache would quietly shrink to whatever GitHub happened to touch since the switch. Re-embedding
    /// uses the stored title and body excerpt rather than a second GitHub pass: for the issues that
    /// fit the excerpt (nearly all of them) the text is exactly what a fresh sync would send, and a
    /// longer issue gets a vector built from its first 1000 body characters until its next real edit
    /// re-embeds it in full. Batched and flushed like the main loop, and equally free to fail: the
    /// caller's catch leaves the watermark where it was, so the next sync picks the rest up.
    /// </summary>
    private async Task<int> ReembedMismatchedModelsAsync(string repoKey, CancellationToken ct)
    {
        var stale = await db.IssueEmbeddings
            .Where(e => e.RepoKey == repoKey && e.EmbeddingModel != _embeddingModel)
            .ToListAsync(ct);

        var sinceLastSave = 0;
        foreach (var row in stale)
        {
            // The hash describes text that has not changed, so it is carried across unchanged; only
            // the vector and the model stamp move.
            await EmbedAsync(row, row.Title, row.BodyExcerpt, row.ContentHash, ct);

            if (++sinceLastSave < saveBatchSize) continue;

            await db.SaveChangesAsync(ct);
            sinceLastSave = 0;
        }

        // The tail rides along on the watermark save, exactly as the main loop's does.
        return stale.Count;
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
        var body = SemanticBody(issue.Body);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issue.Title + "\n" + body)));

        if (!rows.TryGetValue(issue.Number, out var row))
        {
            row = new IssueEmbedding
            {
                RepoKey = repoKey, IssueNumber = issue.Number,
                Title = issue.Title, State = issue.State, ContentHash = "",
            };
            db.IssueEmbeddings.Add(row);
            rows[issue.Number] = row;
            await EmbedAsync(row, issue.Title, body, hash, ct);
        }
        // A row embedded by a different model is re-embedded even though its text is unchanged: the
        // vector, not the content, is what went stale.
        else if (row.ContentHash != hash || row.EmbeddingModel != _embeddingModel)
        {
            await EmbedAsync(row, issue.Title, body, hash, ct);
        }

        // Metadata is refreshed on every sync even when the content hash is unchanged: an issue can
        // be closed or renamed-back without its embedded text differing from what we cached.
        row.Title = issue.Title;
        row.State = issue.State;
        row.ClosedAtUtc = issue.ClosedAtUtc;
        row.UpdatedAtUtc = issue.UpdatedAtUtc;
        row.HtmlUrl = issue.HtmlUrl;
        row.BodyExcerpt = Truncate(body, BodyExcerptLength);
    }

    /// <summary>
    /// The part of an issue body that says what the issue is about. Bodies this bot composed end with
    /// generated boilerplate — the attribution footer, a screenshot gallery, an upload-failure note, a
    /// regression reference — which is near-identical across every issue it files: embedded, it is a
    /// shared vector component that pulls unrelated bot-created issues towards each other, and quoted
    /// into the judge's prompt it is a thousand characters of noise per candidate. Everything from
    /// <see cref="IssueBodyComposer.MetaMarker"/> onwards is therefore dropped. A body without the
    /// marker — every human-authored issue, and every issue filed before the marker existed — is used
    /// whole, so this only ever narrows what the bot itself wrote.
    /// </summary>
    private static string SemanticBody(string body)
    {
        var marker = body.IndexOf(IssueBodyComposer.MetaMarker, StringComparison.Ordinal);
        return marker < 0 ? body : body[..marker].Trim();
    }

    private async Task EmbedAsync(
        IssueEmbedding row, string title, string body, string hash, CancellationToken ct)
    {
        var text = Truncate(title + "\n\n" + body, EmbedTextLength);
        var vector = await embedder.GenerateVectorAsync(text, cancellationToken: ct);
        row.Vector = vector.ToArray();

        // The hash and the model stamp advance only once the vector they describe is in hand, so a
        // failed embedding leaves the row looking stale and the next sync retries it instead of
        // skipping it forever — and never claims a vector came from a model that never produced it.
        row.ContentHash = hash;
        row.EmbeddingModel = _embeddingModel;
    }

    /// <summary>Repo keys are stored lowercase so lookups never depend on how the repo was configured.</summary>
    private static string NormalizeRepoKey(string repo) => repo.ToLowerInvariant();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
