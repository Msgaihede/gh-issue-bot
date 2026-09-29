using System.Text.Json;
using DiscordGithubBot.Ai;
using DiscordGithubBot.CodeContext;
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
/// <param name="Labels">repository labels the issue will carry; shown on the preview</param>
/// <param name="Match">set for Match</param>
/// <param name="Candidates">set for Uncertain (1..5 items); empty otherwise</param>
public sealed record ReportOutcome(
    ReportOutcomeKind Kind, Guid PendingReportId, IssueDraft Draft, ReportType Type, IReadOnlyList<string> Labels,
    CandidateIssue? Match, IReadOnlyList<CandidateIssue> Candidates);

/// <summary>Everything the models decided about a report, before anything is stored or shown.</summary>
/// <param name="OpenIssues">the open issues the duplicate finder compared the draft with</param>
public sealed record ReportAnalysis(
    ReportType Type, NormalizedReport Normalized, DraftReview Review, DuplicateVerdict Verdict,
    IReadOnlyList<CachedIssue> OpenIssues)
{
    /// <summary>The draft as it would ship: the reviewed title over the normalized body.</summary>
    public IssueDraft Draft => new(Review.Title, Normalized.Body);
}

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

    /// <summary>The model work of <see cref="ProcessAsync"/> without storing anything; what <c>--dry-run</c> prints.</summary>
    /// <exception cref="NormalizationException"/>
    Task<ReportAnalysis> AnalyzeAsync(AppConfig app, string rawText, CancellationToken ct = default);

    /// <summary>Confirm-create: uploads images, adds code context, creates the GitHub issue, deletes the pending report.</summary>
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
    IDraftReviewer reviewer,
    IIssueSyncService sync,
    IDuplicateFinder duplicates,
    IPendingReportStore store,
    IGitHubService gitHub,
    IImageUploader imageUploader,
    IAdditionalInfoExtractor extractor,
    ICodeContextPrefetcher codeContext,
    AiUsageMeter usage,
    BotOptions options,
    ILogger<ReportPipeline> logger) : IReportPipeline
{
    public async Task<ReportOutcome> ProcessAsync(ReportSubmission submission, CancellationToken ct = default)
    {
        var app = submission.App;
        var analysis = await AnalyzeAsync(app, submission.RawText, ct);
        var (type, _, review, verdict, openIssues) = analysis;
        var draft = analysis.Draft;

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
            LabelsJson = JsonSerializer.Serialize(review.Labels),
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

        // Built while the reporter reads the preview, so "Create issue" usually finds it ready. Started for
        // every outcome: a reporter shown a duplicate can still choose "Not it — show my draft".
        codeContext.Start(pending.Id, app, draft);

        logger.LogInformation(
            "Drafted {Type} report {ReportId} for {Repo} from {Reporter}: \"{Title}\", {Labels}; {Verdict} over {Open} open issue(s); AI usage {Usage}.",
            type, ShortId(pending.Id), app.Repo, submission.ReporterDisplayName, draft.Title,
            review.Labels.Count == 0 ? "no labels" : "labels " + string.Join(", ", review.Labels),
            DescribeVerdict(verdict), openIssues.Count, usage);
        return Route(pending.Id, draft, type, review.Labels, verdict, shortlist);
    }

    public async Task<ReportAnalysis> AnalyzeAsync(AppConfig app, string rawText, CancellationToken ct = default)
    {
        // The issue sync and the label listing are GitHub I/O, so they run alongside the model calls.
        // Both swallow GitHub failures: dedup then runs against the cache as it stands, and the issue
        // simply goes without labels.
        var syncing = sync.SyncAsync(app, ct);
        var listingLabels = ListLabelsQuietlyAsync(app, ct);

        ReportType type;
        NormalizedReport normalized;
        try
        {
            type = await classifier.ClassifyAsync(app.Name, rawText, ct);

            // A failed normalization throws: a half-written issue is worse than none, so the Discord
            // layer turns NormalizationException into an ephemeral error instead of drafting anything.
            normalized = await normalizer.NormalizeAsync(type, app.Name, rawText, ct);
        }
        finally
        {
            // Awaited on every path: the sync shares this scope's DbContext, which must not be disposed
            // under it when normalization fails.
            await syncing;
        }

        var openIssues = await sync.GetOpenIssuesAsync(app.Repo, ct);

        // Independent decision calls over the same draft, so they run together. Dedup reads the draft
        // with its first title: the body carries the substance, and waiting for the title choice would
        // put a second round trip in front of the reporter for no gain.
        var reviewing = reviewer.ReviewAsync(app, type, normalized, await listingLabels, ct);
        var finding = duplicates.FindAsync(new IssueDraft(normalized.Titles[0], normalized.Body), openIssues, ct);
        await Task.WhenAll(reviewing, finding);

        return new ReportAnalysis(type, normalized, await reviewing, await finding, openIssues);
    }

    public async Task<CreatedIssueResult> CreateIssueAsync(Guid pendingReportId, CancellationToken ct = default)
    {
        var (report, app) = await ClaimAsync(pendingReportId, ct);

        try
        {
            // The code context was started in the background when the preview was shown, and is written into
            // the GitHub issue only (never shown in Discord). Usually it is ready; if the reporter was quick it
            // is awaited here, alongside the uploads. WhenAll waits for both even when one fails, so the claim
            // release below never races the database.
            var uploading = UploadAttachmentsAsync(app, report, ct);
            var enriching = codeContext.GetAsync(report, app, ct);
            await Task.WhenAll(uploading, enriching);
            var (images, failedUploads) = await uploading;

            var body = IssueBodyComposer.ComposeIssueBody(
                report.DraftBody, report.ReporterDisplayName, report.GuildName, images, failedUploads, await enriching);
            var issue = await gitHub.CreateIssueAsync(app, report.DraftTitle, body, Labels(report), ct);
            await store.DeleteAsync(pendingReportId, ct);

            logger.LogInformation(
                "Created issue #{Number} in {Repo} from report {ReportId} by {Reporter} ({Images} image(s){Failed}): {Url}; AI usage {Usage}.",
                issue.Number, app.Repo, ShortId(pendingReportId), report.ReporterDisplayName, images.Count,
                failedUploads.Count == 0 ? "" : $", {failedUploads.Count} failed", issue.HtmlUrl, usage);
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
                "Commented on issue #{Number} in {Repo} from report {ReportId} by {Reporter}: {Url}; AI usage {Usage}.",
                issueNumber, app.Repo, ShortId(pendingReportId), report.ReporterDisplayName, commentUrl, usage);
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

    /// <summary>The label names chosen at submit time; a row that cannot be read carries none.</summary>
    public static IReadOnlyList<string> Labels(PendingReport report)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(report.LabelsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The repository's labels, or none when GitHub cannot list them — a report never fails over labels.</summary>
    private async Task<IReadOnlyList<RepoLabel>> ListLabelsQuietlyAsync(AppConfig app, CancellationToken ct)
    {
        try
        {
            return await gitHub.ListLabelsAsync(app, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Listing labels for {Repo} failed; the issue will carry none.", app.Repo);
            return [];
        }
    }

    public async Task CancelAsync(Guid pendingReportId, CancellationToken ct = default)
    {
        await store.DeleteAsync(pendingReportId, ct);
        logger.LogInformation("Report {ReportId} was cancelled by its reporter.", ShortId(pendingReportId));
    }

    /// <summary>The first eight hex digits: enough to follow one report through the log, short enough to read.</summary>
    internal static string ShortId(Guid id) => id.ToString("N")[..8];

    private static string DescribeVerdict(DuplicateVerdict verdict) => verdict.Kind switch
    {
        VerdictKind.Match => $"duplicate of #{verdict.IssueNumber}",
        VerdictKind.Uncertain => $"maybe a duplicate of #{string.Join(", #", verdict.CandidateNumbers)}",
        _ => "no duplicate",
    };

    public Task<PendingReport?> PeekAsync(Guid pendingReportId, CancellationToken ct = default) =>
        store.GetAsync(pendingReportId, ct);

    /// <summary>Turns the duplicate verdict into the flow the Discord layer should show.</summary>
    private ReportOutcome Route(
        Guid id, IssueDraft draft, ReportType type, IReadOnlyList<string> labels,
        DuplicateVerdict verdict, IReadOnlyList<CandidateIssue> shortlist)
    {
        var none = new ReportOutcome(ReportOutcomeKind.NoMatch, id, draft, type, labels, null, []);

        switch (verdict.Kind)
        {
            case VerdictKind.Match when shortlist.FirstOrDefault(c => c.Number == verdict.IssueNumber) is { } match:
                return none with { Kind = ReportOutcomeKind.Match, Match = match };

            case VerdictKind.Match:
                // The finder only matches an issue it shortlisted, so this is a contract violation rather
                // than an expected path — asking the reporter beats acting on an unknown issue.
                logger.LogWarning(
                    "The duplicate finder matched #{Number}, which was not among the candidates; asking the reporter.",
                    verdict.IssueNumber);
                return Uncertain(none, shortlist);

            case VerdictKind.Uncertain:
                // Kept in the finder's order, most likely first, which is the order the pick list shows.
                return Uncertain(none, verdict.CandidateNumbers
                    .Select(n => shortlist.FirstOrDefault(c => c.Number == n))
                    .OfType<CandidateIssue>()
                    .ToList());

            default:
                return none;
        }
    }

    /// <summary>Uncertain needs something to pick from; with nothing to show it is just a preview.</summary>
    private static ReportOutcome Uncertain(ReportOutcome none, IReadOnlyList<CandidateIssue> candidates) =>
        candidates.Count > 0 ? none with { Kind = ReportOutcomeKind.Uncertain, Candidates = candidates } : none;

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
