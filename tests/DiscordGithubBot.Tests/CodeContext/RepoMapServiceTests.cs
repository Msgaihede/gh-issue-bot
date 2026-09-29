using System.Text.Json;
using DiscordGithubBot.CodeContext;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordGithubBot.Tests.CodeContext;

public sealed class RepoMapServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly BotDbContext _db;
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();

    private static readonly AppConfig App = new() { Name = "MyApp", Repo = "Owner/Repo", GitHubToken = "p" };

    public RepoMapServiceTests()
    {
        _conn.Open();
        _db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _gitHub.GetBlobTextAsync(App, Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => $"contents of {call.ArgAt<string>(1)}");
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private RepoMapService Sut(FakeChat chat) => new(_db, _gitHub, chat, NullLogger<RepoMapService>.Instance);

    private void Head(string commit, params TreeFile[] files)
    {
        _gitHub.GetDefaultBranchHeadAsync(App, Arg.Any<CancellationToken>()).Returns(new BranchHead("main", commit, $"tree-{commit}"));
        _gitHub.GetTreeAsync(App, $"tree-{commit}", Arg.Any<CancellationToken>()).Returns(new RepoTree(files, false));
    }

    private static TreeFile File(string path, string sha) => new(path, sha, 100);

    /// <summary>Answers each summary call by summarizing every file its prompt carries.</summary>
    private sealed class EchoChat(List<ChatPrompt> seen) : IOpenRouterChat
    {
        public List<ChatUrgency> Urgencies { get; } = new();

        public Task<T> CompleteAsync<T>(ChatPrompt prompt, ChatUrgency urgency, CancellationToken ct = default) where T : class
        {
            seen.Add(prompt);
            Urgencies.Add(urgency);
            var paths = prompt.User.Split('\n').Where(l => l.StartsWith("=== ")).Select(l => l[4..^4]);
            var json = JsonSerializer.Serialize(new { files = paths.Select(p => new { path = p, summary = $"About {p}" }) });
            return Task.FromResult(StructuredOutput.Parse<T>(json)!);
        }
    }

    private Dictionary<string, RepoFile> Rows() => _db.RepoFiles.AsNoTracking().ToDictionary(f => f.Path);

    [Fact]
    public async Task A_first_refresh_summarizes_every_source_file_and_doc_and_completes()
    {
        Head("c1", File("src/a.cs", "s1"), File("docs/setup.md", "s2"), File("logo.png", "s3"));
        var seen = new List<ChatPrompt>();
        var chat = new EchoChat(seen);

        var result = await new RepoMapService(_db, _gitHub, chat, NullLogger<RepoMapService>.Instance).RefreshAsync(App);

        Assert.True(result.Complete);
        Assert.Equal(2, result.Summarized);
        var rows = Rows();
        Assert.Equal(["docs/setup.md", "src/a.cs"], rows.Keys.Order());
        Assert.Equal("About src/a.cs", rows["src/a.cs"].Summary);
        Assert.Equal("owner/repo", rows["src/a.cs"].RepoKey);

        var state = _db.RepoMapStates.Single();
        Assert.Equal("c1", state.CommitSha);
        Assert.True(state.IsComplete);

        Assert.Contains("contents of s1", Assert.Single(seen).User);
        // Background work waits out the flex queue rather than paying for the regular tier.
        Assert.Equal([ChatUrgency.Background], chat.Urgencies);
    }

    [Fact]
    public async Task An_unchanged_head_costs_no_tree_and_no_summaries()
    {
        Head("c1", File("src/a.cs", "s1"));
        var seen = new List<ChatPrompt>();
        var sut = new RepoMapService(_db, _gitHub, new EchoChat(seen), NullLogger<RepoMapService>.Instance);
        await sut.RefreshAsync(App);

        var result = await sut.RefreshAsync(App);

        Assert.True(result.UpToDate);
        Assert.Single(seen);
        await _gitHub.Received(1).GetTreeAsync(App, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>git's own identity decides what is re-read: only changed blobs are summarized again.</summary>
    [Fact]
    public async Task A_new_commit_resummarizes_only_changed_files_and_drops_deleted_ones()
    {
        var seen = new List<ChatPrompt>();
        var sut = new RepoMapService(_db, _gitHub, new EchoChat(seen), NullLogger<RepoMapService>.Instance);
        Head("c1", File("src/a.cs", "a1"), File("src/b.cs", "b1"), File("src/c.cs", "c1"));
        await sut.RefreshAsync(App);

        Head("c2", File("src/a.cs", "a1"), File("src/b.cs", "b2"));
        var result = await sut.RefreshAsync(App);

        Assert.Equal(1, result.Summarized);
        Assert.Equal(1, result.Removed);
        Assert.Equal(["src/a.cs", "src/b.cs"], Rows().Keys.Order());
        Assert.Equal("b2", Rows()["src/b.cs"].BlobSha);
        // Each row keeps the commit it was read at: the unchanged file's link still shows what was summarized.
        Assert.Equal("c1", Rows()["src/a.cs"].CommitSha);
        Assert.Equal("c2", Rows()["src/b.cs"].CommitSha);
        Assert.DoesNotContain("=== src/a.cs", seen[1].User);
        Assert.Equal("c2", _db.RepoMapStates.Single().CommitSha);
    }

    [Fact]
    public async Task A_binary_file_is_stored_unsummarized_and_never_sent_to_the_model()
    {
        Head("c1", File("src/a.cs", "bin"));
        _gitHub.GetBlobTextAsync(App, "bin", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var seen = new List<ChatPrompt>();

        var result = await new RepoMapService(_db, _gitHub, new EchoChat(seen), NullLogger<RepoMapService>.Instance)
            .RefreshAsync(App);

        Assert.True(result.Complete);
        Assert.Empty(seen);
        Assert.Equal("", Rows()["src/a.cs"].Summary);
    }

    /// <summary>Asking again would most likely pay for the same omission, every pass.</summary>
    [Fact]
    public async Task A_file_the_model_skipped_is_stored_unsummarized_rather_than_retried()
    {
        Head("c1", File("src/a.cs", "s1"), File("src/b.cs", "s2"));
        var chat = new FakeChat("""{"files":[{"path":"src/a.cs","summary":"Checkout."}]}""");

        var result = await Sut(chat).RefreshAsync(App);

        Assert.True(result.Complete);
        Assert.Equal("Checkout.", Rows()["src/a.cs"].Summary);
        Assert.Equal("", Rows()["src/b.cs"].Summary);
    }

    [Fact]
    public async Task A_transient_failure_ends_the_pass_incomplete_so_the_next_one_resumes()
    {
        Head("c1", File("src/a.cs", "s1"));

        var result = await Sut(new FakeChat(new OpenRouterException("queue", null, isTransient: true))).RefreshAsync(App);

        Assert.False(result.Complete);
        Assert.Empty(Rows());
        Assert.False(_db.RepoMapStates.Single().IsComplete);
    }

    /// <summary>
    /// Exhausted credits, a bad key or an unknown model say nothing about the files. Parking them would blank the
    /// map the moment the credit limit — the intended hard cap — is reached, and keep it blank after a top-up.
    /// </summary>
    [Theory]
    [InlineData(System.Net.HttpStatusCode.PaymentRequired)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.BadRequest)]
    public async Task A_request_openrouter_refuses_ends_the_pass_without_parking_anything(System.Net.HttpStatusCode status)
    {
        Head("c1", File("src/a.cs", "s1"));

        var result = await Sut(new FakeChat(new OpenRouterException("refused", status, isTransient: false))).RefreshAsync(App);

        Assert.False(result.Complete);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task A_permanent_failure_parks_the_batch_so_no_pass_pays_for_it_again()
    {
        Head("c1", File("src/a.cs", "s1"));

        var result = await Sut(new FakeChat(new OpenRouterException("refused", null, isTransient: false))).RefreshAsync(App);

        Assert.True(result.Complete);
        Assert.Equal("", Rows()["src/a.cs"].Summary);
    }

    [Fact]
    public async Task A_github_failure_is_swallowed()
    {
        _gitHub.GetDefaultBranchHeadAsync(App, Arg.Any<CancellationToken>())
            .Returns<BranchHead>(_ => throw new HttpRequestException("down"));

        var result = await Sut(new FakeChat("{}")).RefreshAsync(App);

        Assert.False(result.Complete);
    }

    [Fact]
    public async Task A_large_first_build_is_spread_over_several_passes()
    {
        var files = Enumerable.Range(0, RepoMapService.MaxSummariesPerRefresh + 10)
            .Select(i => File($"src/f{i:D4}.cs", $"s{i}")).ToArray();
        Head("c1", files);
        var sut = new RepoMapService(_db, _gitHub, new EchoChat([]), NullLogger<RepoMapService>.Instance);

        var first = await sut.RefreshAsync(App);
        var second = await sut.RefreshAsync(App);

        Assert.False(first.Complete);
        Assert.Equal(RepoMapService.MaxSummariesPerRefresh, first.Summarized);
        Assert.True(second.Complete);
        Assert.Equal(10, second.Summarized);
    }

    [Fact]
    public async Task Summaries_are_batched()
    {
        var files = Enumerable.Range(0, RepoMapService.MaxFilesPerBatch + 1)
            .Select(i => File($"src/f{i:D2}.cs", $"s{i}")).ToArray();
        Head("c1", files);
        var seen = new List<ChatPrompt>();

        await new RepoMapService(_db, _gitHub, new EchoChat(seen), NullLogger<RepoMapService>.Instance).RefreshAsync(App);

        Assert.Equal(2, seen.Count);
    }
}
