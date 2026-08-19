using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordGithubBot.Tests.Pipeline;

public sealed class IssueSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly BotDbContext _db;
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly FakeEmbeddingGenerator _embedder = new();
    private readonly IssueSyncService _sut;

    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "p",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    public IssueSyncServiceTests()
    {
        _conn.Open();
        _db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _sut = new IssueSyncService(_db, _gitHub, _embedder, NullLogger<IssueSyncService>.Instance);
    }

    private static GitHubIssue Issue(int n, string title = "t", string body = "b", string state = "open",
        DateTime? closedAt = null) =>
        new(n, title, body, state, DateTime.UtcNow, closedAt, $"https://github.com/owner/repo/issues/{n}");

    [Fact]
    public async Task First_sync_embeds_all_issues_and_records_sync_state()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>())
            .Returns([Issue(1), Issue(2)]);

        await _sut.SyncAsync(App);

        Assert.Equal(2, _db.IssueEmbeddings.Count());
        Assert.All(_db.IssueEmbeddings, e => Assert.NotEmpty(e.Vector));
        Assert.NotNull(_db.RepoSyncStates.Find("owner/repo"));
        Assert.Equal(2, _embedder.Inputs.Count);
    }

    [Fact]
    public async Task Second_sync_passes_since_and_skips_reembedding_unchanged_content()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>()).Returns([Issue(1)]);
        await _sut.SyncAsync(App);

        _gitHub.ListIssuesAsync(App, "all", Arg.Is<DateTime?>(d => d != null), Arg.Any<CancellationToken>())
            .Returns([Issue(1)]); // same title+body -> same hash
        await _sut.SyncAsync(App);

        Assert.Equal(1, _db.IssueEmbeddings.Count());
        Assert.Single(_embedder.Inputs); // no second embedding call
    }

    [Fact]
    public async Task Changed_content_is_reembedded_and_state_updated()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>()).Returns([Issue(1)]);
        await _sut.SyncAsync(App);

        _gitHub.ListIssuesAsync(App, "all", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns([Issue(1, title: "new title", state: "closed", closedAt: DateTime.UtcNow)]);
        await _sut.SyncAsync(App);

        var e = _db.IssueEmbeddings.Single();
        Assert.Equal("new title", e.Title);
        Assert.Equal("closed", e.State);
        Assert.Equal(2, _embedder.Inputs.Count);
    }

    [Fact]
    public async Task Bot_generated_boilerplate_is_kept_out_of_the_embedding_and_the_excerpt()
    {
        const string draft = "The save button does nothing on mobile.";
        var composed = IssueBodyComposer.ComposeIssueBody(
            draft, "markus", "Acme HQ", [new UploadedImage("shot.png", "https://x/shot")], ["bad.png"], null);
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>())
            .Returns([Issue(1, title: "Save is broken", body: composed)]);

        await _sut.SyncAsync(App);

        var embedded = Assert.Single(_embedder.Inputs);
        Assert.Equal("Save is broken\n\n" + draft, embedded);
        Assert.DoesNotContain("Created by", embedded);
        Assert.DoesNotContain("Screenshots", embedded);
        Assert.DoesNotContain(IssueBodyComposer.MetaMarker, embedded);

        // The excerpt feeds the judge's prompt, so it is trimmed to the same semantic body.
        Assert.Equal(draft, _db.IssueEmbeddings.Single().BodyExcerpt);
    }

    [Fact]
    public async Task A_human_authored_body_is_embedded_whole()
    {
        // No marker means nothing to strip — an issue filed by hand, or filed before the marker
        // existed, must reach the model exactly as GitHub returned it.
        const string body = "Steps:\n\n1. Click save\n\n---\nCreated by me, in a footer I wrote myself.";
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>())
            .Returns([Issue(1, title: "T", body: body)]);

        await _sut.SyncAsync(App);

        Assert.Equal("T\n\n" + body, Assert.Single(_embedder.Inputs));
        Assert.Equal(body, _db.IssueEmbeddings.Single().BodyExcerpt);
    }

    [Fact]
    public async Task Boilerplate_churn_under_an_unchanged_report_costs_no_new_embedding()
    {
        const string draft = "The save button does nothing on mobile.";
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>())
            .Returns([Issue(1, body: IssueBodyComposer.ComposeIssueBody(draft, "markus", "Acme HQ", [], [], null))]);
        await _sut.SyncAsync(App);

        // Same report; a screenshot gallery, an upload note, a regression line and a renamed server
        // appear behind it. All of it is ours, none of it changes what the issue is about.
        _gitHub.ListIssuesAsync(App, "all", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns([Issue(1, body: IssueBodyComposer.ComposeIssueBody(
                draft, "markus", "Acme HQ (renamed)",
                [new UploadedImage("a.png", "https://x/a")], ["b.png"], 7))]);
        await _sut.SyncAsync(App);

        Assert.Single(_embedder.Inputs);
    }

    [Fact]
    public async Task GitHub_failure_is_swallowed_and_cache_left_intact()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>()).Returns([Issue(1)]);
        await _sut.SyncAsync(App);

        _gitHub.ListIssuesAsync(App, "all", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<GitHubIssue>>>(_ => throw new HttpRequestException("down"));
        await _sut.SyncAsync(App); // must not throw

        Assert.Equal(1, _db.IssueEmbeddings.Count());
    }

    [Fact]
    public async Task Candidates_include_open_and_recently_closed_but_prune_old_closed()
    {
        _db.IssueEmbeddings.AddRange(
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 1, Title = "open", State = "open", ContentHash = "h", Vector = [1f] },
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 2, Title = "recent", State = "closed", ClosedAtUtc = DateTime.UtcNow.AddDays(-5), ContentHash = "h", Vector = [1f] },
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 3, Title = "old", State = "closed", ClosedAtUtc = DateTime.UtcNow.AddDays(-45), ContentHash = "h", Vector = [1f] },
            new IssueEmbedding { RepoKey = "other/repo", IssueNumber = 4, Title = "foreign", State = "open", ContentHash = "h", Vector = [1f] });
        await _db.SaveChangesAsync();

        var candidates = await _sut.GetCandidatesAsync("owner/repo");

        Assert.Equal([1, 2], candidates.Select(c => c.IssueNumber).Order().ToArray());
        Assert.Null(await _db.IssueEmbeddings.SingleOrDefaultAsync(e => e.IssueNumber == 3)); // pruned
    }

    [Fact]
    public async Task Http_timeout_is_swallowed_like_any_github_failure()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>()).Returns([Issue(1)]);
        await _sut.SyncAsync(App);

        // HttpClient reports its own timeout as a TaskCanceledException even though nobody cancelled.
        _gitHub.ListIssuesAsync(App, "all", Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<GitHubIssue>>>(
                _ => throw new TaskCanceledException("timeout", new TimeoutException()));
        await _sut.SyncAsync(App, CancellationToken.None); // must not throw

        Assert.Equal(1, _db.IssueEmbeddings.Count());
    }

    [Fact]
    public async Task A_failed_cold_sync_keeps_the_batches_it_finished_but_not_the_watermark()
    {
        var embedder = new FailingEmbedder(failOnCall: 3);
        var sut = new IssueSyncService(
            _db, _gitHub, embedder, NullLogger<IssueSyncService>.Instance, saveBatchSize: 2);
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>())
            .Returns([Issue(1), Issue(2), Issue(3), Issue(4)]);

        await sut.SyncAsync(App); // swallowed by contract

        Assert.Equal([1, 2], _db.IssueEmbeddings.Select(e => e.IssueNumber).OrderBy(n => n).ToArray());
        Assert.Null(_db.RepoSyncStates.Find("owner/repo")); // the window must be retried in full

        embedder.Healthy = true;
        await sut.SyncAsync(App);

        Assert.Equal([1, 2, 3, 4], _db.IssueEmbeddings.Select(e => e.IssueNumber).OrderBy(n => n).ToArray());
        Assert.NotNull(_db.RepoSyncStates.Find("owner/repo"));
    }

    [Fact]
    public async Task Cancellation_propagates_and_leaves_no_half_written_rows_tracked()
    {
        using var cts = new CancellationTokenSource();
        var sut = new IssueSyncService(_db, _gitHub, new CancellingEmbedder(cts), NullLogger<IssueSyncService>.Instance);
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>()).Returns([Issue(1)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.SyncAsync(App, cts.Token));

        Assert.DoesNotContain(_db.ChangeTracker.Entries(), e => e.State == EntityState.Added);
        await _db.SaveChangesAsync();
        Assert.Empty(_db.IssueEmbeddings);
    }

    /// <summary>Fails the nth embedding call the way a rate-limited endpoint would, until healed.</summary>
    private sealed class FailingEmbedder(int failOnCall) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly FakeEmbeddingGenerator _inner = new();
        private int _calls;

        public bool Healthy { get; set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (!Healthy && ++_calls == failOnCall) throw new HttpRequestException("429 Too Many Requests");
            return _inner.GenerateAsync(values, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Cancels mid-sync, after the first row has been added to the change tracker.</summary>
    private sealed class CancellingEmbedder(CancellationTokenSource cts)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }
}
