using System.Text.Json;
using DiscordGithubBot.Ai;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Pipeline;

/// <summary>A Discord attachment already downloaded into memory; CDN URLs expire, bytes do not.</summary>
public sealed record AttachmentPayload(string FileName, string ContentType, byte[] Bytes);

public sealed record ReportSubmission(
    AppConfig App, ReportType Type, ulong DiscordUserId,
    string ReporterDisplayName, string RawText,
    IReadOnlyList<AttachmentPayload> Attachments);

/// <summary>A dedup candidate as shown to the user; serialized into PendingReport.CandidatesJson.</summary>
public sealed record CandidateIssue(int Number, string Title, string State, string Url);

public enum ReportOutcomeKind { MatchOpen, MatchClosed, Uncertain, NoMatch }

/// <param name="Match">set for MatchOpen/MatchClosed</param>
/// <param name="Candidates">set for Uncertain (1..5 items); empty otherwise</param>
public sealed record ReportOutcome(
    ReportOutcomeKind Kind, Guid PendingReportId, IssueDraft Draft,
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
    /// <summary>Modal submit -> normalized draft -> dedup verdict. Persists a PendingReport and returns the routed outcome.</summary>
    Task<ReportOutcome> ProcessAsync(ReportSubmission submission, CancellationToken ct = default);

    /// <summary>Confirm-create: uploads images, creates the GitHub issue, deletes the pending report.</summary>
    /// <exception cref="ExpiredPendingReportException"/>
    Task<CreatedIssueResult> CreateIssueAsync(Guid pendingReportId, int? regressionOfIssueNumber, CancellationToken ct = default);

    /// <summary>Confirm-duplicate: uploads images, comments on the existing issue, deletes the pending report.</summary>
    /// <exception cref="ExpiredPendingReportException"/>
    Task<CommentResult> AddCommentAsync(Guid pendingReportId, int issueNumber, CancellationToken ct = default);

    /// <summary>Cancel: drops the pending report if it still exists.</summary>
    Task CancelAsync(Guid pendingReportId, CancellationToken ct = default);

    /// <summary>Non-destructive read of pending state (draft, candidates, repo) for component handlers; null when unknown or expired.</summary>
    Task<PendingReport?> PeekAsync(Guid pendingReportId, CancellationToken ct = default);
}

/// <summary>
/// Runs a report end to end: normalize, embed, rank the cached issues, ask the judge, and park the
/// draft as a <see cref="PendingReport"/> so a later button click can finish the job. Nothing reaches
/// GitHub from <see cref="ProcessAsync"/> — the reporter always confirms first, and the pending row is
/// only dropped once GitHub has accepted the issue or comment, so a failed call leaves the draft intact
/// for a retry.
/// </summary>
public sealed class ReportPipeline(
    IReportNormalizer normalizer,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IIssueSyncService sync,
    IDuplicateJudge judge,
    IPendingReportStore store,
    IGitHubService gitHub,
    IImageUploader imageUploader,
    BotOptions options,
    ILogger<ReportPipeline> logger) : IReportPipeline
{
    /// <summary>How many ranked issues are shown to the judge and offered to the reporter.</summary>
    private const int MaxCandidates = 5;

    public async Task<ReportOutcome> ProcessAsync(ReportSubmission submission, CancellationToken ct = default)
    {
        // A failed normalization throws: a half-written issue is worse than none, so the Discord
        // layer turns NormalizationException into an ephemeral error instead of drafting anything.
        var draft = await normalizer.NormalizeAsync(submission.Type, submission.App.Name, submission.RawText, ct);

        var queryVector = await embedder.GenerateVectorAsync(
            draft.Title + "\n\n" + draft.Body, cancellationToken: ct);

        // Sync first so the candidate set includes issues filed since the last report; it swallows
        // GitHub failures by contract, in which case dedup runs against the cache as it stands.
        await sync.SyncAsync(submission.App, ct);
        var candidates = await sync.GetCandidatesAsync(submission.App.Repo, ct);

        var ranked = VectorRanker.TopK(queryVector, candidates, MaxCandidates);
        var verdict = await judge.JudgeAsync(draft, ranked.Select(r => r.Issue).ToList(), ct);

        var shortlist = ranked
            .Select(r => new CandidateIssue(r.Issue.IssueNumber, r.Issue.Title, r.Issue.State, r.Issue.HtmlUrl))
            .ToList();

        var pending = new PendingReport
        {
            Id = Guid.NewGuid(),
            RepoKey = submission.App.Repo,
            DiscordUserId = submission.DiscordUserId,
            ReporterDisplayName = submission.ReporterDisplayName,
            Type = submission.Type,
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

        return Route(pending.Id, draft, verdict, shortlist);
    }

    public async Task<CreatedIssueResult> CreateIssueAsync(
        Guid pendingReportId, int? regressionOfIssueNumber, CancellationToken ct = default)
    {
        var (report, app) = await ClaimAsync(pendingReportId, ct);

        try
        {
            var (images, failedUploads) = await UploadAttachmentsAsync(app, report, ct);

            var body = IssueBodyComposer.ComposeIssueBody(
                report.DraftBody, report.ReporterDisplayName, images, failedUploads, regressionOfIssueNumber);
            var label = report.Type == ReportType.Bug ? "bug" : "enhancement";

            var issue = await gitHub.CreateIssueAsync(app, report.DraftTitle, body, label, ct);
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
                report.DraftBody, report.ReporterDisplayName, images, failedUploads);

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

    public Task CancelAsync(Guid pendingReportId, CancellationToken ct = default) =>
        store.DeleteAsync(pendingReportId, ct);

    public Task<PendingReport?> PeekAsync(Guid pendingReportId, CancellationToken ct = default) =>
        store.GetAsync(pendingReportId, ct);

    /// <summary>Turns the judge's verdict into the flow the Discord layer should show.</summary>
    private ReportOutcome Route(
        Guid id, IssueDraft draft, DuplicateVerdict verdict, IReadOnlyList<CandidateIssue> shortlist) =>
        verdict.Kind switch
        {
            VerdictKind.Match => Matched(id, draft, verdict.IssueNumber, shortlist),
            VerdictKind.Uncertain => Uncertain(
                id, draft, shortlist.Where(c => verdict.CandidateNumbers.Contains(c.Number)).ToList()),
            _ => NoMatch(id, draft),
        };

    private ReportOutcome Matched(
        Guid id, IssueDraft draft, int? issueNumber, IReadOnlyList<CandidateIssue> shortlist)
    {
        var match = shortlist.FirstOrDefault(c => c.Number == issueNumber);
        if (match is null)
        {
            // The judge only ever matches an issue it was offered, so this is a contract violation
            // rather than an expected path — asking the reporter beats acting on an unknown issue.
            logger.LogWarning(
                "The duplicate judge matched #{Number}, which was not among the candidates; asking the reporter.",
                issueNumber);
            return Uncertain(id, draft, shortlist);
        }

        // An issue closed inside the candidate window gets the "is it still happening?" flow instead
        // of a plain duplicate link, so a regression is filed as a new issue referencing the old one.
        var kind = string.Equals(match.State, "open", StringComparison.OrdinalIgnoreCase)
            ? ReportOutcomeKind.MatchOpen
            : ReportOutcomeKind.MatchClosed;

        return new ReportOutcome(kind, id, draft, match, []);
    }

    /// <summary>Uncertain needs something to pick from; with nothing to show it is just a preview.</summary>
    private static ReportOutcome Uncertain(Guid id, IssueDraft draft, IReadOnlyList<CandidateIssue> shortlist) =>
        shortlist.Count > 0
            ? new ReportOutcome(ReportOutcomeKind.Uncertain, id, draft, null, shortlist)
            : NoMatch(id, draft);

    private static ReportOutcome NoMatch(Guid id, IssueDraft draft) =>
        new(ReportOutcomeKind.NoMatch, id, draft, null, []);

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
