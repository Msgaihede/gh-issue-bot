using System.Text.Json;
using DiscordGithubBot.Ai;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.OpenRouter;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Pipeline;

/// <summary>A Discord attachment already downloaded into memory; CDN URLs expire, bytes do not.</summary>
public sealed record AttachmentPayload(string FileName, string ContentType, byte[] Bytes);

/// <param name="GuildName">
/// Name of the Discord server the report came from; credited alongside the reporter in the GitHub
/// footer. Empty when the interaction carried no guild the bot knows, which drops the server half of the footer.
/// </param>
public sealed record ReportSubmission(
    AppConfig App, ulong DiscordUserId,
    string ReporterDisplayName, string GuildName, string RawText,
    IReadOnlyList<AttachmentPayload> Attachments);

/// <summary>A dedup candidate as shown to the user; serialized into PendingReport.CandidatesJson.</summary>
public sealed record CandidateIssue(int Number, string Title, string Url);

public enum ReportOutcomeKind { Match, Uncertain, NoMatch }

/// <param name="Type">what the decision model classified the report as; shown on the preview</param>
/// <param name="Match">set for Match</param>
/// <param name="Candidates">set for Uncertain (1..5 items); empty otherwise</param>
public sealed record ReportOutcome(
    ReportOutcomeKind Kind, Guid PendingReportId, IssueDraft Draft, ReportType Type,
    CandidateIssue? Match, IReadOnlyList<CandidateIssue> Candidates);

/// <param name="Images">screenshots that made it to GitHub, in the order the reporter attached them;
/// the channel announcement shows them as a media gallery</param>
public sealed record CreatedIssueResult(
    int Number, string Title, string HtmlUrl, IReadOnlyList<UploadedImage> Images);

public sealed record CommentResult(int IssueNumber, string CommentUrl);

/// <summary>
/// The report a click referred to is not available: unknown, past its one-hour life, or already claimed
/// by another click that is talking to GitHub right now. All three read the same to a reporter — the
/// buttons no longer do anything — so they share one exception rather than one per cause.
/// </summary>
public sealed class ExpiredPendingReportException()
    : Exception("This report is no longer available — it expired, or another click is already handling it.");

public interface IReportPipeline
{
    /// <summary>Modal submit -> type -> normalized draft -> dedup verdict. Persists a PendingReport and returns the routed outcome.</summary>
    Task<ReportOutcome> ProcessAsync(ReportSubmission submission, CancellationToken ct = default);

    /// <summary>Confirm-create: uploads images, creates the GitHub issue, deletes the pending report.</summary>
    /// <exception cref="ExpiredPendingReportException"/>
    Task<CreatedIssueResult> CreateIssueAsync(Guid pendingReportId, CancellationToken ct = default);

    /// <summary>Confirm-duplicate: uploads images, comments on the existing issue, deletes the pending report.</summary>
    /// <exception cref="ExpiredPendingReportException"/>
    Task<CommentResult> AddCommentAsync(Guid pendingReportId, int issueNumber, CancellationToken ct = default);

    /// <summary>Cancel: drops the pending report if it still exists.</summary>
    Task CancelAsync(Guid pendingReportId, CancellationToken ct = default);

    /// <summary>Non-destructive read of pending state (draft, candidates, repo) for component handlers; null when unknown or expired.</summary>
    Task<PendingReport?> PeekAsync(Guid pendingReportId, CancellationToken ct = default);
}

/// <summary>
/// Runs a report end to end: classify it, draft it, check it against the repo's open issues, and park
/// the draft as a <see cref="PendingReport"/> so a later button click can finish the job. Nothing reaches
/// GitHub from <see cref="ProcessAsync"/> — the reporter always confirms first, and the pending row is
/// only dropped once GitHub has accepted the issue or comment, so a failed call leaves the draft intact
/// for a retry.
/// </summary>
public sealed class ReportPipeline(
    IReportClassifier classifier,
    IReportNormalizer normalizer,
    IIssueSyncService sync,
    IDuplicateFinder duplicates,
    IPendingReportStore store,
    IGitHubService gitHub,
    IImageUploader imageUploader,
    IAdditionalInfoExtractor extractor,
    AiUsageMeter usage,
    BotOptions options,
    ILogger<ReportPipeline> logger) : IReportPipeline
{
    public async Task<ReportOutcome> ProcessAsync(ReportSubmission submission, CancellationToken ct = default)
    {
        var app = submission.App;

        // The issue sync is GitHub I/O and the only step here that touches the database, so it runs
        // alongside the model calls. It swallows GitHub failures by contract, in which case dedup runs
        // against the cache as it stands.
        var syncing = sync.SyncAsync(app, ct);

        ReportType type;
        NormalizedReport normalized;
        try
        {
            type = await classifier.ClassifyAsync(app.Name, submission.RawText, ct);

            // A failed normalization throws: a half-written issue is worse than none, so the Discord
            // layer turns NormalizationException into an ephemeral error instead of drafting anything.
            normalized = await normalizer.NormalizeAsync(type, app.Name, submission.RawText, ct);
        }
        finally
        {
            // Awaited on every path: the sync shares this scope's DbContext, which must not be disposed
            // under it when normalization fails.
            await syncing;
        }

        var draft = new IssueDraft(normalized.Titles[0], normalized.Body);
        var openIssues = await sync.GetOpenIssuesAsync(app.Repo, ct);
        var verdict = await duplicates.FindAsync(draft, openIssues, ct);

        var byNumber = openIssues.ToDictionary(i => i.IssueNumber);
        var shortlist = verdict.Shortlist
            .Where(byNumber.ContainsKey)
            .Select(n => new CandidateIssue(n, byNumber[n].Title, byNumber[n].HtmlUrl))
            .ToList();

        var pending = new PendingReport
        {
            Id = Guid.NewGuid(),
            RepoKey = app.Repo,
            DiscordUserId = submission.DiscordUserId,
            ReporterDisplayName = submission.ReporterDisplayName,
            GuildName = submission.GuildName,
            Type = type,
            OriginalText = submission.RawText,
            DraftTitle = draft.Title,
            DraftBody = draft.Body,
            // The whole shortlist is stored, not just the routed subset: a reporter who answers
            // "none of these" must still be able to act on the draft without a second dedup pass.
            CandidatesJson = JsonSerializer.Serialize(shortlist),
            CreatedAtUtc = DateTime.UtcNow,
            Attachments = submission.Attachments
                .Select(a => new PendingAttachment
                {
                    FileName = a.FileName, ContentType = a.ContentType, Bytes = a.Bytes,
                })
                .ToList(),
        };

        await store.SaveAsync(pending, ct);

        logger.LogInformation(
            "Drafted a {Type} report for {Repo}: {Verdict} over {Open} open issue(s); AI cost ${Cost} in {Calls} call(s).",
            type, app.Repo, verdict.Kind, openIssues.Count, usage.TotalCost, usage.Calls);
        return Route(pending.Id, draft, type, verdict, shortlist);
    }

    public async Task<CreatedIssueResult> CreateIssueAsync(Guid pendingReportId, CancellationToken ct = default)
    {
        var (report, app) = await ClaimAsync(pendingReportId, ct);

        try
        {
            var (images, failedUploads) = await UploadAttachmentsAsync(app, report, ct);

            var body = IssueBodyComposer.ComposeIssueBody(
                report.DraftBody, report.ReporterDisplayName, report.GuildName, images, failedUploads);
            var label = report.Type == ReportType.Bug ? "bug" : "enhancement";

            var issue = await gitHub.CreateIssueAsync(app, report.DraftTitle, body, [label], ct);
            await store.DeleteAsync(pendingReportId, ct);

            logger.LogInformation(
                "Created issue #{Number} in {Repo} for {Reporter}.", issue.Number, app.Repo, report.ReporterDisplayName);
            return new CreatedIssueResult(issue.Number, issue.Title, issue.HtmlUrl, images);
        }
        catch
        {
            await ReleaseClaimQuietlyAsync(pendingReportId);
            throw;
        }
    }

    public async Task<CommentResult> AddCommentAsync(
        Guid pendingReportId, int issueNumber, CancellationToken ct = default)
    {
        var (report, app) = await ClaimAsync(pendingReportId, ct);

        try
        {
            var (images, failedUploads) = await UploadAttachmentsAsync(app, report, ct);

            var body = IssueBodyComposer.ComposeCommentBody(
                await AdditionalInfoOrDraftAsync(report, issueNumber, ct),
                report.ReporterDisplayName, report.GuildName, images, failedUploads);

            var commentUrl = await gitHub.AddCommentAsync(app, issueNumber, body, ct);
            await store.DeleteAsync(pendingReportId, ct);

            logger.LogInformation(
                "Commented on issue #{Number} in {Repo} for {Reporter}.",
                issueNumber, app.Repo, report.ReporterDisplayName);
            return new CommentResult(issueNumber, commentUrl);
        }
        catch
        {
            await ReleaseClaimQuietlyAsync(pendingReportId);
            throw;
        }
    }

    /// <summary>
    /// What a duplicate comment should say: only what the report adds to the matched issue, and "" when
    /// it adds nothing (the composer then posts the attribution line alone). The comparison runs against
    /// the cached title and body excerpt — the same text the duplicate finder read. When the issue is
    /// missing from the cache (closed since, say) or extraction fails, the full draft is posted instead:
    /// a redundant comment is recoverable, silently dropped details are not.
    /// </summary>
    private async Task<string> AdditionalInfoOrDraftAsync(
        PendingReport report, int issueNumber, CancellationToken ct)
    {
        var openIssues = await sync.GetOpenIssuesAsync(report.RepoKey, ct);
        var existing = openIssues.FirstOrDefault(c => c.IssueNumber == issueNumber);
        if (existing is null)
        {
            logger.LogWarning(
                "Issue #{Number} is not in the open-issue cache for {Repo}; commenting with the full draft.",
                issueNumber, report.RepoKey);
            return report.DraftBody;
        }

        var info = await extractor.ExtractAsync(
            new IssueDraft(report.DraftTitle, report.DraftBody),
            existing.Title, existing.BodyExcerpt, ct);
        if (info is null)
        {
            logger.LogWarning(
                "Additional-info extraction failed for issue #{Number}; commenting with the full draft.",
                issueNumber);
            return report.DraftBody;
        }

        return info;
    }

    public Task CancelAsync(Guid pendingReportId, CancellationToken ct = default) =>
        store.DeleteAsync(pendingReportId, ct);

    public Task<PendingReport?> PeekAsync(Guid pendingReportId, CancellationToken ct = default) =>
        store.GetAsync(pendingReportId, ct);

    /// <summary>Turns the duplicate verdict into the flow the Discord layer should show.</summary>
    private ReportOutcome Route(
        Guid id, IssueDraft draft, ReportType type, DuplicateVerdict verdict, IReadOnlyList<CandidateIssue> shortlist) =>
        verdict.Kind switch
        {
            VerdictKind.Match => Matched(id, draft, type, verdict.IssueNumber, shortlist),
            // Kept in the finder's order, most likely first, which is the order the pick list shows.
            VerdictKind.Uncertain => Uncertain(id, draft, type, verdict.CandidateNumbers
                .Select(n => shortlist.FirstOrDefault(c => c.Number == n))
                .OfType<CandidateIssue>()
                .ToList()),
            _ => NoMatch(id, draft, type),
        };

    private ReportOutcome Matched(
        Guid id, IssueDraft draft, ReportType type, int? issueNumber, IReadOnlyList<CandidateIssue> shortlist)
    {
        var match = shortlist.FirstOrDefault(c => c.Number == issueNumber);
        if (match is not null) return new ReportOutcome(ReportOutcomeKind.Match, id, draft, type, match, []);

        // The finder only matches an issue it shortlisted, so this is a contract violation rather than an
        // expected path — asking the reporter beats acting on an unknown issue.
        logger.LogWarning(
            "The duplicate finder matched #{Number}, which was not among the candidates; asking the reporter.",
            issueNumber);
        return Uncertain(id, draft, type, shortlist);
    }

    /// <summary>Uncertain needs something to pick from; with nothing to show it is just a preview.</summary>
    private static ReportOutcome Uncertain(
        Guid id, IssueDraft draft, ReportType type, IReadOnlyList<CandidateIssue> candidates) =>
        candidates.Count > 0
            ? new ReportOutcome(ReportOutcomeKind.Uncertain, id, draft, type, null, candidates)
            : NoMatch(id, draft, type);

    private static ReportOutcome NoMatch(Guid id, IssueDraft draft, ReportType type) =>
        new(ReportOutcomeKind.NoMatch, id, draft, type, null, []);

    /// <summary>
    /// Takes exclusive ownership of a pending report and finds the app that owns its repository. Every
    /// path out of this method either returns a claim the caller must release or throws having released
    /// nothing, which is why the app lookup is inside the same try.
    /// </summary>
    /// <exception cref="ExpiredPendingReportException">unknown, expired, or claimed by another click</exception>
    private async Task<(PendingReport Report, AppConfig App)> ClaimAsync(Guid pendingReportId, CancellationToken ct)
    {
        var report = await store.TryClaimAsync(pendingReportId, ct) ?? throw new ExpiredPendingReportException();

        var app = options.AppByRepo(report.RepoKey);
        if (app is not null) return (report, app);

        await ReleaseClaimQuietlyAsync(pendingReportId);
        throw new InvalidOperationException($"No app is configured for repository '{report.RepoKey}'.");
    }

    /// <summary>
    /// Hands a claim back after a failed attempt, preserving decision 27: a GitHub call that fails must
    /// cost the reporter nothing, so the draft, its screenshots and its buttons all stay usable. The
    /// release runs on <see cref="CancellationToken.None"/> because it is compensation for a failure that
    /// may itself have been a cancellation, and it swallows its own errors so it never replaces the real
    /// exception with a bookkeeping one — a claim left behind is cleaned up with the report at expiry.
    /// </summary>
    private async Task ReleaseClaimQuietlyAsync(Guid pendingReportId)
    {
        try
        {
            await store.ReleaseClaimAsync(pendingReportId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Could not release the claim on pending report {PendingId}; it will expire instead.",
                pendingReportId);
        }
    }

    /// <summary>
    /// Uploads the report's screenshots one by one. A failed upload is collected as a file name, never
    /// thrown: losing a screenshot must not cost the reporter their issue.
    /// </summary>
    private async Task<(List<UploadedImage> Images, List<string> FailedUploads)> UploadAttachmentsAsync(
        AppConfig app, PendingReport report, CancellationToken ct)
    {
        var images = new List<UploadedImage>();
        var failedUploads = new List<string>();

        // Sequential rather than parallel: a handful of screenshots against one repo, and the gallery
        // keeps the order the reporter attached them in.
        foreach (var attachment in report.Attachments)
        {
            var uploaded = await imageUploader.UploadAsync(
                app, attachment.FileName, attachment.ContentType, attachment.Bytes, ct);

            if (uploaded is null)
            {
                logger.LogWarning(
                    "Screenshot {FileName} could not be uploaded for {Repo}; noting it in the body.",
                    attachment.FileName, app.Repo);
                failedUploads.Add(attachment.FileName);
            }
            else
            {
                images.Add(uploaded);
            }
        }

        return (images, failedUploads);
    }
}
