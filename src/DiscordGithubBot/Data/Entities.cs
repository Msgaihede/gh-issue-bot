namespace DiscordGithubBot.Data;

/// <summary>What kind of report a user submitted.</summary>
public enum ReportType
{
    Bug,
    Feature,
}

/// <summary>
/// An open GitHub issue as duplicate detection reads it. Only open issues are kept: dedup compares new
/// reports against open issues alone, and the sync deletes a row as soon as its issue closes.
/// </summary>
public class CachedIssue
{
    public int Id { get; set; }

    /// <summary>Repository in "owner/repo" form, lowercase.</summary>
    public required string RepoKey { get; set; }

    public int IssueNumber { get; set; }
    public required string Title { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// The start of the issue body — the reporter's half only, for issues this bot filed (see
    /// <c>IssueSyncService.SemanticBody</c>). What the duplicate finder reads.
    /// </summary>
    public string BodyExcerpt { get; set; } = "";

    public string HtmlUrl { get; set; } = "";
}

/// <summary>A drafted issue awaiting the reporter's confirmation.</summary>
public class PendingReport
{
    public Guid Id { get; set; }
    public required string RepoKey { get; set; }
    public ulong DiscordUserId { get; set; }
    public required string ReporterDisplayName { get; set; }

    /// <summary>
    /// Name of the Discord server the report was filed in, credited in the GitHub footer. Stored with
    /// the draft rather than read at confirmation time: the click that creates the issue may land after
    /// a rename, and the footer should say where the report was actually made. Empty when the
    /// interaction carried no guild.
    /// </summary>
    public string GuildName { get; set; } = "";

    public ReportType Type { get; set; }
    public required string OriginalText { get; set; }
    public required string DraftTitle { get; set; }
    public required string DraftBody { get; set; }

    /// <summary>Serialized repository label names chosen for the issue, attached when it is created.</summary>
    public string LabelsJson { get; set; } = "[]";

    /// <summary>Serialized duplicate candidates shown to the reporter.</summary>
    public string CandidatesJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When a confirmation click took ownership of this report, or null while it is still up for grabs.
    /// Two clicks can land at the same instant; the one that wins the claim is the one that talks to
    /// GitHub, and a failed attempt clears the field so the reporter can press the button again.
    /// </summary>
    public DateTime? ClaimedAtUtc { get; set; }

    public List<PendingAttachment> Attachments { get; set; } = new();
}

/// <summary>Bytes of a Discord attachment, stored because CDN URLs expire.</summary>
public class PendingAttachment
{
    public int Id { get; set; }
    public Guid PendingReportId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Bytes { get; set; }
}

/// <summary>When a repository's issues were last synced.</summary>
public class RepoSyncState
{
    public required string RepoKey { get; set; }

    /// <summary>Watermark for the next incremental pass (GitHub's <c>since</c>).</summary>
    public DateTime LastSyncUtc { get; set; }

    /// <summary>When every open issue was last listed and the cache replaced with exactly those.</summary>
    public DateTime LastFullSyncUtc { get; set; }
}
