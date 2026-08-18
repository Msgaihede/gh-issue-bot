namespace DiscordGithubBot.Data;

/// <summary>What kind of report a user submitted.</summary>
public enum ReportType
{
    Bug,
    Feature,
}

/// <summary>A GitHub issue plus its cached embedding, used for duplicate detection.</summary>
public class IssueEmbedding
{
    public int Id { get; set; }

    /// <summary>Repository in "owner/repo" form, lowercase.</summary>
    public required string RepoKey { get; set; }

    public int IssueNumber { get; set; }
    public required string Title { get; set; }

    /// <summary>"open" or "closed".</summary>
    public required string State { get; set; }

    public DateTime? ClosedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>SHA256 hex of title + "\n" + body; lets sync skip unchanged issues.</summary>
    public required string ContentHash { get; set; }

    /// <summary>First 1000 characters of the issue body.</summary>
    public string BodyExcerpt { get; set; } = "";

    public string HtmlUrl { get; set; } = "";

    /// <summary>Embedding vector; persisted as a BLOB via <see cref="VectorConversion"/>.</summary>
    public float[] Vector { get; set; } = [];
}

/// <summary>A drafted issue awaiting the reporter's confirmation.</summary>
public class PendingReport
{
    public Guid Id { get; set; }
    public required string RepoKey { get; set; }
    public ulong DiscordUserId { get; set; }
    public required string ReporterDisplayName { get; set; }
    public ReportType Type { get; set; }
    public required string OriginalText { get; set; }
    public required string DraftTitle { get; set; }
    public required string DraftBody { get; set; }

    /// <summary>Serialized duplicate candidates shown to the reporter.</summary>
    public string CandidatesJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; }
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
    public DateTime LastSyncUtc { get; set; }
}
