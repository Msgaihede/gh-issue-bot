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

    private const string Model = "text-embedding-3-small";

    private static readonly AppConfig App = new()
    {
        Name = "MyApp", Repo = "owner/repo", GitHubToken = "p",
        GuildIds = [1UL], ChannelIds = [2UL],
    };

    private static BotOptions Options(string embeddingModel = Model) =>
        new() { OpenAI = { EmbeddingModel = embeddingModel } };

    public IssueSyncServiceTests()
    {
        _conn.Open();
        _db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _sut = new IssueSyncService(_db, _gitHub, _embedder, Options(), NullLogger<IssueSyncService>.Instance);
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

        // The model-heal pass runs on every sync; a row already stamped with the configured model
        // must cost nothing there either.
        Assert.Equal(Model, _db.IssueEmbeddings.Single().EmbeddingModel);
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
    public async Task A_marker_pasted_into_a_report_cannot_hide_the_rest_of_it()
    {
        // The composer strips literal markers out of the draft, so the only marker in a composed body is
        // the one it emits — and the cut lands after the whole report rather than in the middle of it.
        var draft = $"Before.\n\n{IssueBodyComposer.MetaMarker}\n\nAfter.";
        var composed = IssueBodyComposer.ComposeIssueBody(draft, "markus", "Acme HQ", [], [], null);
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>())
            .Returns([Issue(1, title: "T", body: composed)]);

        await _sut.SyncAsync(App);

        // Both halves of the report survive the cut, the pasted marker does not, and the real marker
        // still keeps the footer out. (Removing the marker leaves the blank lines that surrounded it,
        // which markdown collapses into the one paragraph break it already was.)
        var embedded = Assert.Single(_embedder.Inputs);
        Assert.Contains("Before.", embedded);
        Assert.Contains("After.", embedded);
        Assert.DoesNotContain(IssueBodyComposer.MetaMarker, embedded);
        Assert.DoesNotContain("Created by", embedded);
        Assert.Contains("After.", _db.IssueEmbeddings.Single().BodyExcerpt);
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
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 1, Title = "open", State = "open", ContentHash = "h", Vector = [1f], EmbeddingModel = Model },
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 2, Title = "recent", State = "closed", ClosedAtUtc = DateTime.UtcNow.AddDays(-5), ContentHash = "h", Vector = [1f], EmbeddingModel = Model },
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 3, Title = "old", State = "closed", ClosedAtUtc = DateTime.UtcNow.AddDays(-45), ContentHash = "h", Vector = [1f], EmbeddingModel = Model },
            new IssueEmbedding { RepoKey = "other/repo", IssueNumber = 4, Title = "foreign", State = "open", ContentHash = "h", Vector = [1f], EmbeddingModel = Model });
        await _db.SaveChangesAsync();

        var candidates = await _sut.GetCandidatesAsync("owner/repo");

        Assert.Equal([1, 2], candidates.Select(c => c.IssueNumber).Order().ToArray());
        Assert.Null(await _db.IssueEmbeddings.SingleOrDefaultAsync(e => e.IssueNumber == 3)); // pruned
    }

    [Fact]
    public async Task New_embeddings_are_stamped_with_the_model_that_produced_them()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>())
            .Returns([Issue(1), Issue(2, title: "other")]);

        await _sut.SyncAsync(App);

        Assert.All(_db.IssueEmbeddings, e => Assert.Equal(Model, e.EmbeddingModel));
    }

    [Fact]
    public async Task Candidates_exclude_rows_embedded_by_another_model()
    {
        // Vectors from two models share no coordinate space, so the mismatched row is not a candidate
        // — but it stays in the cache, because the next sync re-embeds it rather than refetching it.
        _db.IssueEmbeddings.AddRange(
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 1, Title = "current", State = "open", ContentHash = "h", Vector = [1f], EmbeddingModel = Model },
            new IssueEmbedding { RepoKey = "owner/repo", IssueNumber = 2, Title = "legacy", State = "open", ContentHash = "h", Vector = [1f], EmbeddingModel = "text-embedding-ada-002" });
        await _db.SaveChangesAsync();

        var candidates = await _sut.GetCandidatesAsync("owner/repo");

        Assert.Equal([1], candidates.Select(c => c.IssueNumber).ToArray());
        Assert.Equal(2, _db.IssueEmbeddings.Count());
    }

    [Fact]
    public async Task Switching_the_embedding_model_reembeds_stored_rows_even_when_github_returns_nothing()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>()).Returns([Issue(1)]);
        await _sut.SyncAsync(App);
        var hashBefore = _db.IssueEmbeddings.Single().ContentHash;

        // Another repo's row is on the old model too; syncing this app must leave it alone.
        _db.IssueEmbeddings.Add(new IssueEmbedding
        {
            RepoKey = "other/repo", IssueNumber = 9, Title = "foreign", State = "open",
            ContentHash = "h", Vector = [1f], EmbeddingModel = Model,
        });
        await _db.SaveChangesAsync();

        // The operator switches models and restarts. GitHub has nothing new since the watermark, so
        // the incremental pass alone would never look at issue 1 again.
        const string newModel = "text-embedding-3-large";
        var sut = new IssueSyncService(
            _db, _gitHub, _embedder, Options(newModel), NullLogger<IssueSyncService>.Instance);
        _gitHub.ListIssuesAsync(App, "all", Arg.Is<DateTime?>(d => d != null), Arg.Any<CancellationToken>())
            .Returns([]);

        await sut.SyncAsync(App);

        var row = _db.IssueEmbeddings.Single(e => e.RepoKey == "owner/repo");
        Assert.Equal(newModel, row.EmbeddingModel);
        Assert.Equal(hashBefore, row.ContentHash); // the text never changed, only the model did
        Assert.Equal(2, _embedder.Inputs.Count);
        Assert.Equal("t\n\nb", _embedder.Inputs[1]); // re-embedded from the stored title + excerpt
        Assert.Equal(Model, _db.IssueEmbeddings.Single(e => e.RepoKey == "other/repo").EmbeddingModel);

        // Healed rows are candidates again, and a second sync finds nothing left to heal.
        Assert.Single(await sut.GetCandidatesAsync("owner/repo"));
        await sut.SyncAsync(App);
        Assert.Equal(2, _embedder.Inputs.Count);
    }

    [Fact]
    public async Task A_long_body_is_healed_from_its_excerpt_not_from_the_full_text()
    {
        // The documented cost of healing without a GitHub refetch: a body longer than the 1000-char
        // excerpt is re-embedded from its opening only, and stays that way until a real edit.
        var body = new string('a', 1000) + new string('b', 500);
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>())
            .Returns([Issue(1, body: body)]);
        await _sut.SyncAsync(App);

        var sut = new IssueSyncService(
            _db, _gitHub, _embedder, Options("text-embedding-3-large"),
            NullLogger<IssueSyncService>.Instance);
        _gitHub.ListIssuesAsync(App, "all", Arg.Is<DateTime?>(d => d != null), Arg.Any<CancellationToken>())
            .Returns([]);

        await sut.SyncAsync(App);

        // The first pass saw the whole body straight from GitHub; the heal pass saw only the excerpt.
        Assert.Equal(2, _embedder.Inputs.Count);
        Assert.Equal("t\n\n" + body, _embedder.Inputs[0]);
        Assert.Equal("t\n\n" + new string('a', 1000), _embedder.Inputs[1]);
        Assert.DoesNotContain("b", _embedder.Inputs[1]);
    }

    [Fact]
    public async Task A_failed_heal_pass_keeps_its_finished_batches_but_not_the_watermark()
    {
        _gitHub.ListIssuesAsync(App, "all", null, Arg.Any<CancellationToken>())
            .Returns([Issue(1), Issue(2), Issue(3), Issue(4)]);
        await _sut.SyncAsync(App);

        // Pinned to a fixed instant rather than read off the clock, so "did it move?" cannot depend
        // on how coarse DateTime.UtcNow happens to be between two syncs milliseconds apart.
        var state = _db.RepoSyncStates.Find("owner/repo")!;
        var watermark = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        state.LastSyncUtc = watermark;
        await _db.SaveChangesAsync();

        // The model changes and the embedding endpoint rate-limits partway through the heal pass.
        const string newModel = "text-embedding-3-large";
        var embedder = new FailingEmbedder(failOnCall: 3);
        var sut = new IssueSyncService(
            _db, _gitHub, embedder, Options(newModel), NullLogger<IssueSyncService>.Instance,
            saveBatchSize: 2);
        _gitHub.ListIssuesAsync(App, "all", Arg.Is<DateTime?>(d => d != null), Arg.Any<CancellationToken>())
            .Returns([]);

        await sut.SyncAsync(App); // swallowed by contract, exactly like a failed main loop

        // The batch that flushed before the failure keeps its new vectors; the rest are still stale.
        Assert.Equal(2, _db.IssueEmbeddings.Count(e => e.EmbeddingModel == newModel));
        Assert.Equal(2, _db.IssueEmbeddings.Count(e => e.EmbeddingModel == Model));

        // And the watermark never moved, so the pass is retried rather than recorded as done. Read
        // back from the database: a rolled-back tracked value would agree either way.
        await _db.Entry(state).ReloadAsync();
        Assert.Equal(watermark, state.LastSyncUtc);

        // Only the rows still on the old model are re-embedded on the retry, and it completes.
        embedder.Healthy = true;
        await sut.SyncAsync(App);

        Assert.Equal(4, _db.IssueEmbeddings.Count(e => e.EmbeddingModel == newModel));
        await _db.Entry(state).ReloadAsync();
        Assert.True(state.LastSyncUtc > watermark);
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
            _db, _gitHub, embedder, Options(), NullLogger<IssueSyncService>.Instance, saveBatchSize: 2);
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
        var sut = new IssueSyncService(
            _db, _gitHub, new CancellingEmbedder(cts), Options(), NullLogger<IssueSyncService>.Instance);
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
