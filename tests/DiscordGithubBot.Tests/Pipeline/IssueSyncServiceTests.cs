using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DiscordGithubBot.Tests.Pipeline;

public sealed class IssueSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly BotDbContext _db;
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly IssueSyncService _sut;

    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "Owner/Repo", GitHubToken = "p",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    public IssueSyncServiceTests()
    {
        _conn.Open();
        _db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _sut = new IssueSyncService(_db, _gitHub, NullLogger<IssueSyncService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static GitHubIssue Issue(int n, string title = "t", string body = "b", string state = "open") =>
        new(n, title, body, state, DateTime.UtcNow, state == "closed" ? DateTime.UtcNow : null,
            $"https://github.com/owner/repo/issues/{n}");

    private void FullListing(params GitHubIssue[] issues) =>
        _gitHub.ListIssuesAsync(App, "open", null, Arg.Any<CancellationToken>()).Returns(issues);

    private void Updates(params GitHubIssue[] issues) =>
        _gitHub.ListIssuesAsync(App, "all", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>()).Returns(issues);

    private async Task<int[]> OpenNumbers() =>
        (await _sut.GetOpenIssuesAsync("owner/repo")).Select(i => i.IssueNumber).ToArray();

    /// <summary>The first pass needs only open issues; the old embedding cache paged through every closed one too.</summary>
    [Fact]
    public async Task The_first_sync_lists_only_open_issues_and_records_both_watermarks()
    {
        FullListing(Issue(1), Issue(2));

        await _sut.SyncAsync(App);

        Assert.Equal(new[] { 1, 2 }, await OpenNumbers());
        var state = _db.RepoSyncStates.Single();
        Assert.Equal("owner/repo", state.RepoKey);
        Assert.NotEqual(default, state.LastFullSyncUtc);
        await _gitHub.DidNotReceive().ListIssuesAsync(App, "all", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Later_syncs_are_incremental_upserting_open_issues_and_dropping_closed_ones()
    {
        FullListing(Issue(1, title: "old"), Issue(2));
        await _sut.SyncAsync(App);
        var watermark = _db.RepoSyncStates.Single().LastSyncUtc;

        Updates(Issue(1, title: "renamed"), Issue(2, state: "closed"), Issue(3));
        await _sut.SyncAsync(App);

        Assert.Equal(new[] { 1, 3 }, await OpenNumbers());
        Assert.Equal("renamed", (await _sut.GetOpenIssuesAsync("owner/repo"))[0].Title);
        await _gitHub.Received(1).ListIssuesAsync(App, "all", watermark, Arg.Any<CancellationToken>());
    }

    /// <summary>A deleted or transferred issue never shows up in an update feed; the daily full pass clears it.</summary>
    [Fact]
    public async Task A_full_pass_drops_issues_github_no_longer_lists()
    {
        FullListing(Issue(1), Issue(2));
        await _sut.SyncAsync(App);

        _db.RepoSyncStates.Single().LastFullSyncUtc = DateTime.UtcNow.AddDays(-2);
        await _db.SaveChangesAsync();
        FullListing(Issue(2));
        await _sut.SyncAsync(App);

        Assert.Equal(new[] { 2 }, await OpenNumbers());
    }

    [Fact]
    public async Task Bot_generated_boilerplate_is_kept_out_of_the_excerpt()
    {
        const string draft = "The save button does nothing on mobile.";
        var composed = IssueBodyComposer.ComposeIssueBody(
            draft, "markus", "Acme HQ", [new UploadedImage("shot.png", "https://x/shot")], ["bad.png"]);
        FullListing(Issue(1, body: composed));

        await _sut.SyncAsync(App);

        Assert.Equal(draft, _db.CachedIssues.Single().BodyExcerpt);
    }

    [Fact]
    public async Task A_marker_pasted_into_a_report_cannot_hide_the_rest_of_it()
    {
        var draft = $"Before.\n\n{IssueBodyComposer.MetaMarker}\n\nAfter.";
        FullListing(Issue(1, body: IssueBodyComposer.ComposeIssueBody(draft, "markus", "Acme HQ", [], [])));

        await _sut.SyncAsync(App);

        var excerpt = _db.CachedIssues.Single().BodyExcerpt;
        Assert.Contains("Before.", excerpt);
        Assert.Contains("After.", excerpt);
        Assert.DoesNotContain("Created by", excerpt);
    }

    [Fact]
    public async Task A_human_authored_body_is_kept_whole_up_to_the_excerpt_length()
    {
        var body = "Steps:\n\n1. Click save\n\n---\nCreated by me." + new string('x', 5000);
        FullListing(Issue(1, body: body));

        await _sut.SyncAsync(App);

        Assert.Equal(body[..IssueSyncService.BodyExcerptLength], _db.CachedIssues.Single().BodyExcerpt);
    }

    [Fact]
    public async Task A_github_failure_is_swallowed_and_leaves_cache_and_watermark_alone()
    {
        FullListing(Issue(1));
        await _sut.SyncAsync(App);
        var watermark = _db.RepoSyncStates.Single().LastSyncUtc;

        _gitHub.ListIssuesAsync(App, "all", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("down"));
        await _sut.SyncAsync(App);

        Assert.Equal(new[] { 1 }, await OpenNumbers());
        Assert.Equal(watermark, _db.RepoSyncStates.AsNoTracking().Single().LastSyncUtc);
    }

    [Fact]
    public async Task Cancellation_propagates_and_leaves_nothing_half_written_tracked()
    {
        using var cts = new CancellationTokenSource();
        _gitHub.ListIssuesAsync(App, "open", null, Arg.Any<CancellationToken>())
            .Returns(_ => { cts.Cancel(); return Task.FromResult<IReadOnlyList<GitHubIssue>>([Issue(1)]); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _sut.SyncAsync(App, cts.Token));

        Assert.DoesNotContain(_db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    [Fact]
    public async Task Open_issues_are_read_per_repository_case_insensitively()
    {
        FullListing(Issue(5), Issue(4));
        await _sut.SyncAsync(App);
        _db.CachedIssues.Add(new CachedIssue { RepoKey = "other/repo", IssueNumber = 9, Title = "x" });
        await _db.SaveChangesAsync();

        Assert.Equal([4, 5], (await _sut.GetOpenIssuesAsync("OWNER/repo")).Select(i => i.IssueNumber));
    }
}
