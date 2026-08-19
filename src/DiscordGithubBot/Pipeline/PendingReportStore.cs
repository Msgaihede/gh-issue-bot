using DiscordGithubBot.Data;
using Microsoft.EntityFrameworkCore;

namespace DiscordGithubBot.Pipeline;

public interface IPendingReportStore
{
    Task SaveAsync(PendingReport report, CancellationToken ct = default);

    /// <returns>null when unknown OR older than 1 hour (expired rows are deleted on read).</returns>
    Task<PendingReport?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Takes exclusive ownership of a report so only one confirmation click can act on it.
    /// </summary>
    /// <returns>the report, or null when it is unknown, expired, or already claimed.</returns>
    Task<PendingReport?> TryClaimAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gives a claim back, so a failed attempt can be retried from the same buttons.</summary>
    Task ReleaseClaimAsync(Guid id, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Deletes all reports older than 1 hour. Called by the maintenance service.</summary>
    Task<int> CleanupExpiredAsync(CancellationToken ct = default);
}

/// <summary>
/// Persists drafted reports between the modal submit and the reporter's confirmation click. A report
/// is only ever read once the reporter presses a button, so expiry is enforced on read as well as by
/// the periodic cleanup: a stale draft is never resurrected just because the maintenance pass has not
/// run yet.
/// </summary>
public sealed class PendingReportStore(BotDbContext db) : IPendingReportStore
{
    /// <summary>How long a drafted report waits for the reporter before it is considered abandoned.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public async Task SaveAsync(PendingReport report, CancellationToken ct = default)
    {
        db.PendingReports.Add(report);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PendingReport?> GetAsync(Guid id, CancellationToken ct = default)
    {
        // Untracked: callers read the draft and its attachment bytes to build an issue, never to
        // edit it, and tracking would make the change tracker snapshot-clone every attachment blob.
        var report = await db.PendingReports.AsNoTracking()
            .Include(r => r.Attachments)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (report is null) return null;
        if (report.CreatedAtUtc >= Cutoff()) return report;

        await DeleteAsync(id, ct);
        return null;
    }

    /// <summary>
    /// One UPDATE decides the race. The <c>ClaimedAtUtc IS NULL</c> predicate lives in the WHERE clause, so
    /// SQLite — not this process — serializes two simultaneous clicks, and only the statement that reports
    /// one affected row gets to load the report. The expiry test rides along in the same predicate, so a
    /// report cannot be claimed in the moment between expiring and being swept.
    /// </summary>
    public async Task<PendingReport?> TryClaimAsync(Guid id, CancellationToken ct = default)
    {
        var cutoff = Cutoff();
        var now = DateTime.UtcNow;

        var claimed = await db.PendingReports
            .Where(r => r.Id == id && r.ClaimedAtUtc == null && r.CreatedAtUtc >= cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ClaimedAtUtc, now), ct);

        if (claimed != 1) return null;

        // Untracked for the same reason as GetAsync: the caller reads the draft and its attachment bytes
        // to build an issue, never to edit them.
        return await db.PendingReports.AsNoTracking()
            .Include(r => r.Attachments)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task ReleaseClaimAsync(Guid id, CancellationToken ct = default)
    {
        await db.PendingReports
            .Where(r => r.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ClaimedAtUtc, (DateTime?)null), ct);
    }

    /// <summary>
    /// Deletes the report and, through the schema's cascade, its attachment blobs. The rows go with a
    /// single SQL statement rather than being loaded first: every attachment carries a screenshot, and
    /// nothing here needs those bytes in memory to throw them away.
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await db.PendingReports.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = Cutoff();
        return await db.PendingReports.Where(r => r.CreatedAtUtc < cutoff).ExecuteDeleteAsync(ct);
    }

    private static DateTime Cutoff() => DateTime.UtcNow - Lifetime;
}
