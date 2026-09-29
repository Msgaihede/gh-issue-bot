using System.Text.Json;
using DiscordGithubBot.Ai;
using DiscordGithubBot.CodeContext;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordGithubBot.Tests.Pipeline;

public class ReportPipelineTests
{
    private readonly IReportClassifier _classifier = Substitute.For<IReportClassifier>();
    private readonly IReportNormalizer _normalizer = Substitute.For<IReportNormalizer>();
    private readonly IDraftReviewer _reviewer = Substitute.For<IDraftReviewer>();
    private readonly IIssueSyncService _sync = Substitute.For<IIssueSyncService>();
    private readonly IDuplicateFinder _finder = Substitute.For<IDuplicateFinder>();
    private readonly IPendingReportStore _store = Substitute.For<IPendingReportStore>();
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IImageUploader _uploader = Substitute.For<IImageUploader>();
    private readonly IAdditionalInfoExtractor _extractor = Substitute.For<IAdditionalInfoExtractor>();
    private readonly ICodeContextPrefetcher _codeContext = Substitute.For<ICodeContextPrefetcher>();
    private readonly ReportPipeline _sut;

    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "p",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    public ReportPipelineTests()
    {
        _sut = new ReportPipeline(_classifier, _normalizer, _reviewer, _sync, _finder, _store, _gitHub, _uploader, _extractor,
            _codeContext, new AiUsageMeter(), new BotOptions { Apps = [App] }, NullLogger<ReportPipeline>.Instance);
        _classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ReportType.Bug);
        _normalizer.NormalizeAsync(Arg.Any<ReportType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new NormalizedReport(["Draft title", "Other title"], "Draft body"));
        _reviewer.ReviewAsync(Arg.Any<AppConfig>(), Arg.Any<ReportType>(), Arg.Any<NormalizedReport>(),
                Arg.Any<IReadOnlyList<RepoLabel>>(), Arg.Any<CancellationToken>())
            .Returns(new DraftReview("Draft title", ["bug"]));
    }

    private static PendingReport Pending(Guid id) => new()
    {
        Id = id, RepoKey = "owner/repo", DiscordUserId = 42, ReporterDisplayName = "markus",
        GuildName = "Acme HQ", Type = ReportType.Bug, OriginalText = "x", DraftTitle = "T", DraftBody = "B",
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static ReportSubmission Submission(params AttachmentPayload[] attachments) =>
        new(App, 42UL, "markus", "Acme HQ", "it broke", attachments);

    private static CachedIssue Open(int n) => new()
    {
        RepoKey = "owner/repo", IssueNumber = n, Title = $"Issue {n}", BodyExcerpt = $"body {n}",
        HtmlUrl = $"https://github.com/owner/repo/issues/{n}",
    };

    private void SetupOpenIssues(params CachedIssue[] issues) =>
        _sync.GetOpenIssuesAsync("owner/repo", Arg.Any<CancellationToken>()).Returns(issues.ToList());

    private void SetupVerdict(VerdictKind kind, int? match = null, int[]? ask = null, int[]? shortlist = null) =>
        _finder.FindAsync(Arg.Any<IssueDraft>(), Arg.Any<IReadOnlyList<CachedIssue>>(), Arg.Any<CancellationToken>())
            .Returns(new DuplicateVerdict(kind, match, ask ?? [], shortlist ?? []));

    private void SetupExtractor(string? result) =>
        _extractor.ExtractAsync(Arg.Any<IssueDraft>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(result);

    [Fact]
    public async Task No_match_routes_to_preview_with_the_classified_type()
    {
        SetupOpenIssues(Open(1));
        SetupVerdict(VerdictKind.NoMatch, shortlist: [1]);
        _classifier.ClassifyAsync("MyApp", "it broke", Arg.Any<CancellationToken>()).Returns(ReportType.Feature);

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.NoMatch, outcome.Kind);
        Assert.Equal(ReportType.Feature, outcome.Type);
        Assert.Equal("Draft title", outcome.Draft.Title);
        await _normalizer.Received(1).NormalizeAsync(ReportType.Feature, "MyApp", "it broke", Arg.Any<CancellationToken>());
        await _store.Received(1).SaveAsync(Arg.Is<PendingReport>(r =>
            r.DraftTitle == "Draft title" && r.RepoKey == "owner/repo" && r.Type == ReportType.Feature),
            Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default);
    }

    /// <summary>The reviewer's title is the one that ships; its labels ride on the pending report.</summary>
    [Fact]
    public async Task The_reviewed_title_and_labels_are_kept_with_the_draft()
    {
        SetupOpenIssues();
        SetupVerdict(VerdictKind.NoMatch);
        RepoLabel[] repoLabels = [new("bug", ""), new("ui", "")];
        _gitHub.ListLabelsAsync(App, Arg.Any<CancellationToken>()).Returns(repoLabels);
        _reviewer.ReviewAsync(App, ReportType.Bug, Arg.Any<NormalizedReport>(), repoLabels, Arg.Any<CancellationToken>())
            .Returns(new DraftReview("Other title", ["bug", "ui"]));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal("Other title", outcome.Draft.Title);
        Assert.Equal(["bug", "ui"], outcome.Labels);
        await _store.Received(1).SaveAsync(Arg.Is<PendingReport>(r =>
            r.DraftTitle == "Other title" && r.LabelsJson == "[\"bug\",\"ui\"]"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_label_listing_failure_leaves_the_reviewer_with_no_labels_and_the_report_going()
    {
        SetupOpenIssues();
        SetupVerdict(VerdictKind.NoMatch);
        _gitHub.ListLabelsAsync(App, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RepoLabel>>(_ => throw new HttpRequestException("down"));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.NoMatch, outcome.Kind);
        await _reviewer.Received(1).ReviewAsync(App, ReportType.Bug, Arg.Any<NormalizedReport>(),
            Arg.Is<IReadOnlyList<RepoLabel>>(l => l.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_match_routes_to_the_matched_open_issue()
    {
        SetupOpenIssues(Open(7), Open(9));
        SetupVerdict(VerdictKind.Match, match: 7, shortlist: [7, 9]);

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.Match, outcome.Kind);
        Assert.Equal(7, outcome.Match!.Number);
        Assert.Equal("https://github.com/owner/repo/issues/7", outcome.Match.Url);
    }

    [Fact]
    public async Task Uncertain_routes_with_the_finders_candidates_in_its_order()
    {
        SetupOpenIssues(Open(7), Open(9), Open(11));
        SetupVerdict(VerdictKind.Uncertain, ask: [11, 9], shortlist: [11, 9, 7]);

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.Uncertain, outcome.Kind);
        Assert.Equal([11, 9], outcome.Candidates.Select(c => c.Number));
    }

    /// <summary>The finder reads the draft, not the raw text, and every open issue the cache holds.</summary>
    [Fact]
    public async Task The_finder_reads_the_draft_against_every_open_issue_after_the_sync()
    {
        SetupOpenIssues(Open(1), Open(2));
        SetupVerdict(VerdictKind.NoMatch);

        await _sut.ProcessAsync(Submission());

        await _finder.Received(1).FindAsync(
            Arg.Is<IssueDraft>(d => d.Title == "Draft title" && d.Body == "Draft body"),
            Arg.Is<IReadOnlyList<CachedIssue>>(c => c.Count == 2), Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _sync.SyncAsync(App, Arg.Any<CancellationToken>());
            _sync.GetOpenIssuesAsync("owner/repo", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task The_pending_report_keeps_the_shortlist_and_attachments()
    {
        SetupOpenIssues(Open(7), Open(9));
        SetupVerdict(VerdictKind.NoMatch, shortlist: [9, 7]);
        PendingReport? saved = null;
        await _store.SaveAsync(Arg.Do<PendingReport>(r => saved = r), Arg.Any<CancellationToken>());

        var outcome = await _sut.ProcessAsync(
            Submission(new AttachmentPayload("shot.png", "image/png", [1, 2, 3])));

        Assert.NotNull(saved);
        Assert.Equal(outcome.PendingReportId, saved.Id);
        Assert.Equal("it broke", saved.OriginalText);
        Assert.Equal(42UL, saved.DiscordUserId);
        Assert.Equal("Acme HQ", saved.GuildName);
        Assert.Equal("Draft body", saved.DraftBody);

        var candidates = JsonSerializer.Deserialize<List<CandidateIssue>>(saved.CandidatesJson)!;
        Assert.Equal([9, 7], candidates.Select(c => c.Number));
        Assert.Equal("https://github.com/owner/repo/issues/7", candidates[1].Url);

        var attachment = Assert.Single(saved.Attachments);
        Assert.Equal("shot.png", attachment.FileName);
        Assert.Equal<byte[]>([1, 2, 3], attachment.Bytes);
    }

    [Fact]
    public async Task A_normalization_failure_propagates_without_saving_but_after_the_sync_finished()
    {
        var syncDone = false;
        _sync.SyncAsync(App, Arg.Any<CancellationToken>()).Returns(async _ => { await Task.Yield(); syncDone = true; });
        _normalizer.NormalizeAsync(Arg.Any<ReportType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<NormalizedReport>(_ => throw new NormalizationException("no draft"));

        await Assert.ThrowsAsync<NormalizationException>(() => _sut.ProcessAsync(Submission()));

        Assert.True(syncDone);
        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task A_match_on_an_issue_that_was_never_shortlisted_degrades_to_uncertain()
    {
        SetupOpenIssues(Open(7), Open(9));
        SetupVerdict(VerdictKind.Match, match: 404, shortlist: [7, 9]);

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.Uncertain, outcome.Kind);
        Assert.Equal([7, 9], outcome.Candidates.Select(c => c.Number));
    }

    [Fact]
    public async Task Uncertain_without_any_known_candidate_degrades_to_no_match()
    {
        SetupOpenIssues(Open(7));
        SetupVerdict(VerdictKind.Uncertain, ask: [404], shortlist: [7]);

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.NoMatch, outcome.Kind);
    }

    [Fact]
    public async Task CreateIssue_uploads_images_composes_body_attaches_the_chosen_labels_and_deletes_pending()
    {
        var id = Guid.NewGuid();
        var report = Pending(id);
        report.LabelsJson = "[\"bug\",\"android\"]";
        report.Attachments =
        [
            new PendingAttachment { FileName = "ok.png", ContentType = "image/png", Bytes = [1] },
            new PendingAttachment { FileName = "bad.png", ContentType = "image/png", Bytes = [2] },
        ];
        _store.TryClaimAsync(id, Arg.Any<CancellationToken>()).Returns(report);
        _uploader.UploadAsync(App, "ok.png", "image/png", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new UploadedImage("ok.png", "https://gh/ok"));
        _uploader.UploadAsync(App, "bad.png", "image/png", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns((UploadedImage?)null);
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubIssue(101, "T", "B", "open", DateTime.UtcNow, null, "https://gh/101"));

        var result = await _sut.CreateIssueAsync(id);

        Assert.Equal(101, result.Number);
        await _gitHub.Received(1).CreateIssueAsync(App, "T",
            Arg.Is<string>(b => b.Contains("https://gh/ok") && b.Contains("bad.png")
                && b.Contains("_Created by **markus** in Discord server **Acme HQ**._")),
            Arg.Is<IReadOnlyList<string>>(l => l.SequenceEqual(new[] { "bug", "android" })), Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateIssue_appends_the_prefetched_code_context()
    {
        var id = Guid.NewGuid();
        var report = Pending(id);
        _store.TryClaimAsync(id, Arg.Any<CancellationToken>()).Returns(report);
        _codeContext.GetAsync(report, App, Arg.Any<CancellationToken>()).Returns("### Relevant code\n- `src/a.cs`");
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubIssue(101, "T", "B", "open", DateTime.UtcNow, null, "https://gh/101"));

        await _sut.CreateIssueAsync(id);

        await _gitHub.Received(1).CreateIssueAsync(App, "T", Arg.Is<string>(b => b.Contains("### Relevant code")),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The code context is started once the draft is saved, so it is built while the reporter reads the
    /// preview — for every outcome, since a reporter shown a duplicate can still ask for the draft.
    /// </summary>
    [Fact]
    public async Task Drafting_a_report_starts_its_code_context_in_the_background()
    {
        SetupOpenIssues(Open(1));
        SetupVerdict(VerdictKind.Match, match: 1, shortlist: [1]);

        var outcome = await _sut.ProcessAsync(Submission());

        _codeContext.Received(1).Start(outcome.PendingReportId, App,
            Arg.Is<IssueDraft>(d => d.Title == "Draft title" && d.Body == "Draft body"));
    }

    [Fact]
    public async Task A_failed_draft_starts_no_code_context()
    {
        _normalizer.NormalizeAsync(Arg.Any<ReportType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<NormalizedReport>(_ => throw new NormalizationException("no draft"));

        await Assert.ThrowsAsync<NormalizationException>(() => _sut.ProcessAsync(Submission()));

        _codeContext.DidNotReceiveWithAnyArgs().Start(default, default!, default!);
    }

    [Fact]
    public async Task CreateIssue_with_an_unclaimable_pending_report_throws()
    {
        _store.TryClaimAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((PendingReport?)null);
        await Assert.ThrowsAsync<ExpiredPendingReportException>(() => _sut.CreateIssueAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_second_click_loses_the_claim_and_never_reaches_github()
    {
        var id = Guid.NewGuid();
        var claimed = false;
        _store.TryClaimAsync(id, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (claimed) return null;
            claimed = true;
            return Pending(id);
        });
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubIssue(101, "T", "B", "open", DateTime.UtcNow, null, "https://gh/101"));

        await _sut.CreateIssueAsync(id);
        await Assert.ThrowsAsync<ExpiredPendingReportException>(() => _sut.CreateIssueAsync(id));

        await _gitHub.Received(1).CreateIssueAsync(
            App, "T", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_creation_hands_the_claim_back_and_keeps_the_draft()
    {
        var id = Guid.NewGuid();
        _store.TryClaimAsync(id, Arg.Any<CancellationToken>()).Returns(Pending(id));
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns<GitHubIssue>(_ => throw new HttpRequestException("502"));

        await Assert.ThrowsAsync<HttpRequestException>(() => _sut.CreateIssueAsync(id));

        await _store.Received(1).ReleaseClaimAsync(id, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().DeleteAsync(id, Arg.Any<CancellationToken>()); // decision 27
    }

    [Fact]
    public async Task A_failed_comment_hands_the_claim_back_and_keeps_the_draft()
    {
        var id = Guid.NewGuid();
        _store.TryClaimAsync(id, Arg.Any<CancellationToken>()).Returns(Pending(id));
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new HttpRequestException("502"));

        await Assert.ThrowsAsync<HttpRequestException>(() => _sut.AddCommentAsync(id, 7));

        await _store.Received(1).ReleaseClaimAsync(id, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    /// <summary>Claims a pending report whose draft body is distinctive enough to assert on its absence.</summary>
    private Guid ClaimablePending()
    {
        var id = Guid.NewGuid();
        var report = Pending(id);
        report.DraftBody = "TheFullDraftBody";
        _store.TryClaimAsync(id, Arg.Any<CancellationToken>()).Returns(report);
        return id;
    }

    [Fact]
    public async Task AddComment_posts_the_additional_info_not_the_draft_and_deletes_pending()
    {
        var id = ClaimablePending();
        SetupOpenIssues(Open(7));
        SetupExtractor("Only the new detail.");
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("https://gh/7#c1");

        var result = await _sut.AddCommentAsync(id, 7);

        Assert.Equal("https://gh/7#c1", result.CommentUrl);
        await _gitHub.Received(1).AddCommentAsync(App, 7,
            Arg.Is<string>(b => b.Contains("Only the new detail.")
                && !b.Contains("TheFullDraftBody")
                && b.Contains("_Also reported by **markus** in Discord server **Acme HQ**._")),
            Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddComment_with_nothing_new_posts_only_the_attribution_line()
    {
        var id = ClaimablePending();
        SetupOpenIssues(Open(7));
        SetupExtractor("");
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("u");

        await _sut.AddCommentAsync(id, 7);

        await _gitHub.Received(1).AddCommentAsync(App, 7,
            Arg.Is<string>(b => !b.Contains("TheFullDraftBody") && b.Contains("_Also reported by **markus**")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddComment_falls_back_to_the_full_draft_when_extraction_fails()
    {
        var id = ClaimablePending();
        SetupOpenIssues(Open(7));
        SetupExtractor(null);
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("u");

        await _sut.AddCommentAsync(id, 7);

        await _gitHub.Received(1).AddCommentAsync(App, 7,
            Arg.Is<string>(b => b.Contains("TheFullDraftBody")), Arg.Any<CancellationToken>());
    }

    /// <summary>An issue closed since the report was drafted drops out of the open-issue cache.</summary>
    [Fact]
    public async Task AddComment_falls_back_when_the_matched_issue_is_no_longer_cached()
    {
        var id = ClaimablePending();
        SetupOpenIssues();
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("u");

        await _sut.AddCommentAsync(id, 7);

        await _gitHub.Received(1).AddCommentAsync(App, 7,
            Arg.Is<string>(b => b.Contains("TheFullDraftBody")), Arg.Any<CancellationToken>());
        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task AddComment_hands_the_cached_issue_and_the_draft_to_the_extractor()
    {
        var id = ClaimablePending();
        SetupOpenIssues(Open(7));
        SetupExtractor("");
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("u");

        await _sut.AddCommentAsync(id, 7);

        await _extractor.Received(1).ExtractAsync(
            Arg.Is<IssueDraft>(d => d.Title == "T" && d.Body == "TheFullDraftBody"),
            "Issue 7", "body 7", Arg.Any<CancellationToken>());
    }

    /// <summary>A report drafted when the repo's labels could not be read simply goes without.</summary>
    [Fact]
    public async Task A_report_without_labels_is_created_without_labels()
    {
        var id = Guid.NewGuid();
        _store.TryClaimAsync(id, Arg.Any<CancellationToken>()).Returns(Pending(id));
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubIssue(5, "T", "B", "open", DateTime.UtcNow, null, "u"));

        await _sut.CreateIssueAsync(id);

        await _gitHub.Received(1).CreateIssueAsync(App, "T", Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(l => l.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Peek_reads_without_deleting_and_cancel_drops_the_pending_report()
    {
        var id = Guid.NewGuid();
        var report = Pending(id);
        _store.GetAsync(id, Arg.Any<CancellationToken>()).Returns(report);

        Assert.Same(report, await _sut.PeekAsync(id));
        await _store.DidNotReceive().DeleteAsync(id, Arg.Any<CancellationToken>());

        await _sut.CancelAsync(id);
        await _store.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }
}
