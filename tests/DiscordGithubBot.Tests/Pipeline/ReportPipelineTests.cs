using System.Text.Json;
using DiscordGithubBot.Ai;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordGithubBot.Tests.Pipeline;

public class ReportPipelineTests
{
    private readonly IReportNormalizer _normalizer = Substitute.For<IReportNormalizer>();
    private readonly IIssueSyncService _sync = Substitute.For<IIssueSyncService>();
    private readonly IDuplicateJudge _judge = Substitute.For<IDuplicateJudge>();
    private readonly IPendingReportStore _store = Substitute.For<IPendingReportStore>();
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IImageUploader _uploader = Substitute.For<IImageUploader>();
    private readonly IAdditionalInfoExtractor _extractor = Substitute.For<IAdditionalInfoExtractor>();
    private readonly BotOptions _options;
    private readonly ReportPipeline _sut;

    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "p",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    public ReportPipelineTests()
    {
        _options = new BotOptions { Apps = [App] };
        _sut = new ReportPipeline(_normalizer, new FakeEmbeddingGenerator(), _sync, _judge,
            _store, _gitHub, _uploader, _extractor, _options, NullLogger<ReportPipeline>.Instance);
        _normalizer.NormalizeAsync(Arg.Any<ReportType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new IssueDraft("Draft title", "Draft body"));
    }

    private static PendingReport Pending(Guid id) => new()
    {
        Id = id, RepoKey = "owner/repo", DiscordUserId = 42, ReporterDisplayName = "markus",
        GuildName = "Acme HQ", Type = ReportType.Bug, OriginalText = "x", DraftTitle = "T", DraftBody = "B",
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static ReportSubmission Submission(params AttachmentPayload[] attachments) =>
        new(App, ReportType.Bug, 42UL, "markus", "Acme HQ", "it broke", attachments);

    private static IssueEmbedding Candidate(int n, string state = "open", DateTime? closedUtc = null) => new()
    {
        RepoKey = "owner/repo", IssueNumber = n, Title = $"Issue {n}", State = state,
        ClosedAtUtc = closedUtc, ContentHash = "h", BodyExcerpt = $"body {n}",
        HtmlUrl = $"https://github.com/owner/repo/issues/{n}",
        Vector = [0.5f, 0.5f, 0.5f],
    };

    private void SetupCandidates(params IssueEmbedding[] candidates) =>
        _sync.GetCandidatesAsync("owner/repo", Arg.Any<CancellationToken>())
            .Returns(candidates.ToList());

    private void SetupVerdict(DuplicateVerdict verdict) =>
        _judge.JudgeAsync(Arg.Any<IssueDraft>(), Arg.Any<IReadOnlyList<IssueEmbedding>>(), Arg.Any<CancellationToken>())
            .Returns(verdict);

    private void SetupExtractor(string? result) =>
        _extractor.ExtractAsync(Arg.Any<IssueDraft>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(result);

    [Fact]
    public async Task No_match_routes_to_preview()
    {
        SetupCandidates(Candidate(1));
        SetupVerdict(new DuplicateVerdict(VerdictKind.NoMatch, null, []));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.NoMatch, outcome.Kind);
        Assert.Equal("Draft title", outcome.Draft.Title);
        Assert.NotEqual(Guid.Empty, outcome.PendingReportId);
        await _store.Received(1).SaveAsync(Arg.Is<PendingReport>(r =>
            r.DraftTitle == "Draft title" && r.RepoKey == "owner/repo"), Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceiveWithAnyArgs().CreateIssueAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task Match_on_open_issue_routes_to_match_open()
    {
        SetupCandidates(Candidate(7));
        SetupVerdict(new DuplicateVerdict(VerdictKind.Match, 7, []));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.MatchOpen, outcome.Kind);
        Assert.Equal(7, outcome.Match!.Number);
    }

    [Fact]
    public async Task Match_on_recently_closed_issue_routes_to_match_closed()
    {
        SetupCandidates(Candidate(7, "closed", DateTime.UtcNow.AddDays(-3)));
        SetupVerdict(new DuplicateVerdict(VerdictKind.Match, 7, []));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.MatchClosed, outcome.Kind);
        Assert.Equal(7, outcome.Match!.Number);
    }

    [Fact]
    public async Task Uncertain_routes_with_filtered_candidates()
    {
        SetupCandidates(Candidate(7), Candidate(9), Candidate(11));
        SetupVerdict(new DuplicateVerdict(VerdictKind.Uncertain, null, [9, 11]));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.Uncertain, outcome.Kind);
        Assert.Equal([9, 11], outcome.Candidates.Select(c => c.Number).ToArray());
    }

    [Fact]
    public async Task Sync_runs_before_candidates_are_read()
    {
        SetupCandidates();
        SetupVerdict(new DuplicateVerdict(VerdictKind.NoMatch, null, []));
        await _sut.ProcessAsync(Submission());
        Received.InOrder(() =>
        {
            _sync.SyncAsync(App, Arg.Any<CancellationToken>());
            _sync.GetCandidatesAsync("owner/repo", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task CreateIssue_uploads_images_composes_body_and_deletes_pending()
    {
        var id = Guid.NewGuid();
        _store.TryClaimAsync(id, Arg.Any<CancellationToken>()).Returns(new PendingReport
        {
            Id = id, RepoKey = "owner/repo", DiscordUserId = 42, ReporterDisplayName = "markus",
            GuildName = "Acme HQ", Type = ReportType.Bug, OriginalText = "x", DraftTitle = "T", DraftBody = "B",
            CreatedAtUtc = DateTime.UtcNow,
            Attachments =
            [
                new PendingAttachment { FileName = "ok.png", ContentType = "image/png", Bytes = [1] },
                new PendingAttachment { FileName = "bad.png", ContentType = "image/png", Bytes = [2] },
            ],
        });
        _uploader.UploadAsync(App, "ok.png", "image/png", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new UploadedImage("ok.png", "https://gh/ok"));
        _uploader.UploadAsync(App, "bad.png", "image/png", Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns((UploadedImage?)null);
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), "bug", Arg.Any<CancellationToken>())
            .Returns(new GitHubIssue(101, "T", "B", "open", DateTime.UtcNow, null, "https://gh/101"));

        var result = await _sut.CreateIssueAsync(id, regressionOfIssueNumber: 7);

        Assert.Equal(101, result.Number);
        Assert.Equal("https://gh/101", result.HtmlUrl);
        await _gitHub.Received(1).CreateIssueAsync(App, "T",
            Arg.Is<string>(b => b.Contains("https://gh/ok") && b.Contains("bad.png")
                && b.Contains("Possible regression of #7.")
                && b.Contains("_Created by **markus** in Discord server **Acme HQ**._")),
            "bug", Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateIssue_with_an_unclaimable_pending_report_throws()
    {
        // Unknown, expired, and "a second click got here first" all arrive as a null claim.
        _store.TryClaimAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((PendingReport?)null);
        await Assert.ThrowsAsync<ExpiredPendingReportException>(() => _sut.CreateIssueAsync(Guid.NewGuid(), null));
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
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubIssue(101, "T", "B", "open", DateTime.UtcNow, null, "https://gh/101"));

        await _sut.CreateIssueAsync(id, null);
        await Assert.ThrowsAsync<ExpiredPendingReportException>(() => _sut.CreateIssueAsync(id, null));

        await _gitHub.Received(1).CreateIssueAsync(
            App, "T", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_creation_hands_the_claim_back_and_keeps_the_draft()
    {
        var id = Guid.NewGuid();
        _store.TryClaimAsync(id, Arg.Any<CancellationToken>()).Returns(Pending(id));
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<GitHubIssue>(_ => throw new HttpRequestException("502"));

        await Assert.ThrowsAsync<HttpRequestException>(() => _sut.CreateIssueAsync(id, null));

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
        SetupCandidates(Candidate(7));
        SetupExtractor("Only the new detail.");
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://gh/7#c1");

        var result = await _sut.AddCommentAsync(id, 7);

        Assert.Equal("https://gh/7#c1", result.CommentUrl);
        await _gitHub.Received(1).AddCommentAsync(App, 7,
            Arg.Is<string>(b => b.Contains("Only the new detail.")
                && !b.Contains("TheFullDraftBody")
                && b.Contains("_Recreated/experienced by **markus** in Discord server **Acme HQ**._")),
            Arg.Any<CancellationToken>());
        await _store.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddComment_with_nothing_new_posts_only_the_attribution_line()
    {
        var id = ClaimablePending();
        SetupCandidates(Candidate(7));
        SetupExtractor("");
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("u");

        await _sut.AddCommentAsync(id, 7);

        await _gitHub.Received(1).AddCommentAsync(App, 7,
            Arg.Is<string>(b => !b.Contains("TheFullDraftBody")
                && b.Contains("_Recreated/experienced by **markus** in Discord server **Acme HQ**._")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddComment_falls_back_to_the_full_draft_when_extraction_fails()
    {
        var id = ClaimablePending();
        SetupCandidates(Candidate(7));
        SetupExtractor(null);
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("u");

        await _sut.AddCommentAsync(id, 7);

        await _gitHub.Received(1).AddCommentAsync(App, 7,
            Arg.Is<string>(b => b.Contains("TheFullDraftBody")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddComment_falls_back_when_the_matched_issue_is_not_cached()
    {
        var id = ClaimablePending();
        SetupCandidates();
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("u");

        await _sut.AddCommentAsync(id, 7);

        await _gitHub.Received(1).AddCommentAsync(App, 7,
            Arg.Is<string>(b => b.Contains("TheFullDraftBody")),
            Arg.Any<CancellationToken>());
        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task AddComment_hands_the_cached_issue_and_the_draft_to_the_extractor()
    {
        var id = ClaimablePending();
        SetupCandidates(Candidate(7));
        SetupExtractor("");
        _gitHub.AddCommentAsync(App, 7, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("u");

        await _sut.AddCommentAsync(id, 7);

        await _extractor.Received(1).ExtractAsync(
            Arg.Is<IssueDraft>(d => d.Title == "T" && d.Body == "TheFullDraftBody"),
            "Issue 7", "body 7", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Feature_reports_use_enhancement_label()
    {
        var id = Guid.NewGuid();
        _store.TryClaimAsync(id, Arg.Any<CancellationToken>()).Returns(new PendingReport
        {
            Id = id, RepoKey = "owner/repo", DiscordUserId = 1, ReporterDisplayName = "u",
            Type = ReportType.Feature, OriginalText = "x", DraftTitle = "T", DraftBody = "B",
            CreatedAtUtc = DateTime.UtcNow,
        });
        _gitHub.CreateIssueAsync(App, "T", Arg.Any<string>(), "enhancement", Arg.Any<CancellationToken>())
            .Returns(new GitHubIssue(5, "T", "B", "open", DateTime.UtcNow, null, "u"));

        await _sut.CreateIssueAsync(id, null);

        await _gitHub.Received(1).CreateIssueAsync(App, "T", Arg.Any<string>(), "enhancement", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pending_report_keeps_ranked_candidates_and_attachments()
    {
        SetupCandidates(Candidate(7), Candidate(9));
        SetupVerdict(new DuplicateVerdict(VerdictKind.NoMatch, null, []));
        PendingReport? saved = null;
        await _store.SaveAsync(Arg.Do<PendingReport>(r => saved = r), Arg.Any<CancellationToken>());

        var outcome = await _sut.ProcessAsync(
            Submission(new AttachmentPayload("shot.png", "image/png", [1, 2, 3])));

        Assert.NotNull(saved);
        Assert.Equal(outcome.PendingReportId, saved.Id);
        Assert.Equal(ReportType.Bug, saved.Type);
        Assert.Equal("it broke", saved.OriginalText);
        Assert.Equal(42UL, saved.DiscordUserId);
        Assert.Equal("markus", saved.ReporterDisplayName);
        Assert.Equal("Acme HQ", saved.GuildName);
        Assert.Equal("Draft body", saved.DraftBody);

        var candidates = JsonSerializer.Deserialize<List<CandidateIssue>>(saved.CandidatesJson);
        Assert.NotNull(candidates);
        Assert.Equal([7, 9], candidates.Select(c => c.Number).Order().ToArray());
        var seven = candidates.Single(c => c.Number == 7);
        Assert.Equal("Issue 7", seven.Title);
        Assert.Equal("open", seven.State);
        Assert.Equal("https://github.com/owner/repo/issues/7", seven.Url);

        var attachment = Assert.Single(saved.Attachments);
        Assert.Equal("shot.png", attachment.FileName);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal<byte[]>([1, 2, 3], attachment.Bytes);
    }

    [Fact]
    public async Task Only_the_five_best_ranked_candidates_reach_the_judge_and_the_reporter()
    {
        int[] numbers = [1, 2, 3, 4, 5, 6];
        SetupCandidates([.. numbers.Select(n => Candidate(n))]);
        SetupVerdict(new DuplicateVerdict(VerdictKind.Uncertain, null, numbers));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(5, outcome.Candidates.Count);
        await _judge.Received(1).JudgeAsync(Arg.Any<IssueDraft>(),
            Arg.Is<IReadOnlyList<IssueEmbedding>>(c => c.Count == 5), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Normalization_failure_propagates_without_saving_a_pending_report()
    {
        _normalizer.NormalizeAsync(Arg.Any<ReportType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IssueDraft>(_ => throw new NormalizationException("no draft"));

        await Assert.ThrowsAsync<NormalizationException>(() => _sut.ProcessAsync(Submission()));

        await _store.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
    }

    [Fact]
    public async Task Match_on_an_issue_that_was_never_a_candidate_degrades_to_uncertain()
    {
        SetupCandidates(Candidate(7), Candidate(9));
        SetupVerdict(new DuplicateVerdict(VerdictKind.Match, 404, []));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.Uncertain, outcome.Kind);
        Assert.Null(outcome.Match);
        Assert.Equal([7, 9], outcome.Candidates.Select(c => c.Number).Order().ToArray());
    }

    [Fact]
    public async Task Uncertain_without_any_known_candidate_degrades_to_no_match()
    {
        SetupCandidates(Candidate(7));
        SetupVerdict(new DuplicateVerdict(VerdictKind.Uncertain, null, [404]));

        var outcome = await _sut.ProcessAsync(Submission());

        Assert.Equal(ReportOutcomeKind.NoMatch, outcome.Kind);
        Assert.Empty(outcome.Candidates);
    }

    [Fact]
    public async Task Peek_reads_without_deleting_and_cancel_drops_the_pending_report()
    {
        var id = Guid.NewGuid();
        var report = new PendingReport
        {
            Id = id, RepoKey = "owner/repo", DiscordUserId = 1, ReporterDisplayName = "u",
            Type = ReportType.Bug, OriginalText = "x", DraftTitle = "T", DraftBody = "B",
            CreatedAtUtc = DateTime.UtcNow,
        };
        _store.GetAsync(id, Arg.Any<CancellationToken>()).Returns(report);

        Assert.Same(report, await _sut.PeekAsync(id));
        await _store.DidNotReceive().DeleteAsync(id, Arg.Any<CancellationToken>());

        await _sut.CancelAsync(id);
        await _store.Received(1).DeleteAsync(id, Arg.Any<CancellationToken>());
    }
}
