using DiscordGithubBot.Ai;
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

public sealed class CodeContextBuilderTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly BotDbContext _db;
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();

    private static readonly AppConfig App = new() { Name = "MyApp", Repo = "Owner/Repo", GitHubToken = "p" };
    private static readonly IssueDraft Draft = new("Checkout goes blank after tapping Pay", "Tapping Pay shows a blank page.");
    private const string Commit = "abcdef1234567890";

    public CodeContextBuilderTests()
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

    private void Map(params (string Path, string Summary)[] files)
    {
        _db.RepoMapStates.Add(new RepoMapState { RepoKey = "owner/repo", CommitSha = Commit, IsComplete = true });
        foreach (var (path, summary) in files)
            _db.RepoFiles.Add(new RepoFile { RepoKey = "owner/repo", Path = path, BlobSha = "blob-" + path, Summary = summary });
        _db.SaveChanges();
    }

    private int IdOf(string path) => _db.RepoFiles.Single(f => f.Path == path).Id;

    private CodeContextBuilder Sut(FakeDecisions decisions, FakeChat chat) =>
        new(_db, decisions, chat, _gitHub, NullLogger<CodeContextBuilder>.Instance);

    /// <summary>Picks the given paths with the given weight in whichever selection offers them.</summary>
    private FakeDecisions Picks(params (string Path, double P)[] picks) => new((call, _, question) =>
    {
        var options = ((ChoiceQuestion)question).Options.Select(o => o.Key).ToHashSet();
        var weights = picks.Select(p => ($"file_{IdOf(p.Path)}", p.P)).Where(w => options.Contains(w.Item1)).ToList();
        return FakeDecisions.Choice(weights.Append((Shortlist.NoneKey, 1 - weights.Sum(w => w.P))).ToArray());
    });

    [Fact]
    public async Task No_map_means_no_block_and_no_calls()
    {
        var decisions = Picks();

        Assert.Null(await Sut(decisions, new FakeChat("{}")).BuildAsync(App, Draft));
        Assert.Empty(decisions.Calls);
    }

    /// <summary>Code and docs are chosen separately, so a relevant doc never displaces the file that needs fixing.</summary>
    [Fact]
    public async Task Code_and_docs_are_selected_in_separate_questions()
    {
        Map(("src/Checkout.cs", "Checkout flow."), ("src/Cart.cs", "Cart."), ("docs/payments.md", "Payment setup."));
        var decisions = Picks(("src/Checkout.cs", 0.8), ("docs/payments.md", 0.7));
        var chat = new FakeChat("""{"files":[],"notes":""}""");

        await Sut(decisions, chat).BuildAsync(App, Draft);

        var code = decisions.Calls.Single(c => c.Purpose == "code_files");
        var docs = decisions.Calls.Single(c => c.Purpose == "doc_files");
        Assert.Equal(2, code.State["files"]!.AsObject().Count);
        Assert.Equal("docs/payments.md", docs.State["files"]!.AsObject().Single().Value!["path"]!.GetValue<string>());
        Assert.Equal(Draft.Title, code.State["issue"]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Involved_files_are_linked_at_the_mapped_commit_with_their_notes()
    {
        Map(("src/Checkout.cs", "Checkout flow."), ("docs/payments.md", "Payment setup."));
        var chat = new FakeChat("""
            {"files":[
              {"path":"src/Checkout.cs","involved":true,"note":"PayAsync swallows the gateway error."},
              {"path":"docs/payments.md","involved":true,"note":"Documents the retry setting."}],
             "notes":"The fix likely belongs in PayAsync."}
            """);

        var block = await Sut(Picks(("src/Checkout.cs", 0.8), ("docs/payments.md", 0.6)), chat).BuildAsync(App, Draft);

        Assert.NotNull(block);
        Assert.Contains("### Relevant code", block);
        Assert.Contains(
            $"- [`src/Checkout.cs`](https://github.com/Owner/Repo/blob/{Commit}/src/Checkout.cs) — PayAsync swallows the gateway error.",
            block);
        Assert.Contains("### Related docs", block);
        Assert.Contains("docs/payments.md", block);
        Assert.Contains("The fix likely belongs in PayAsync.", block);
        Assert.Contains("`abcdef1`", block);
    }

    [Fact]
    public async Task The_model_reads_the_picked_files_at_the_mapped_blob()
    {
        Map(("src/Checkout.cs", "Checkout flow."));
        var chat = new FakeChat("""{"files":[],"notes":""}""");

        await Sut(Picks(("src/Checkout.cs", 0.9)), chat).BuildAsync(App, Draft);

        var (prompt, urgency) = Assert.Single(chat.Calls);
        Assert.Equal(ChatUrgency.Interactive, urgency);
        Assert.Contains("=== src/Checkout.cs (source) ===", prompt.User);
        Assert.Contains("contents of blob-src/Checkout.cs", prompt.User);
        Assert.Contains(Draft.Body, prompt.User);
    }

    /// <summary>The decision model only ever offers real paths; the chat model's extra ones are dropped.</summary>
    [Fact]
    public async Task Paths_the_model_was_not_given_are_dropped()
    {
        Map(("src/Checkout.cs", "Checkout flow."));
        var chat = new FakeChat("""
            {"files":[
              {"path":"src/Checkout.cs","involved":true,"note":"Real."},
              {"path":"src/Invented.cs","involved":true,"note":"Made up."}],
             "notes":""}
            """);

        var block = await Sut(Picks(("src/Checkout.cs", 0.9)), chat).BuildAsync(App, Draft);

        Assert.DoesNotContain("Invented", block);
    }

    [Fact]
    public async Task Nothing_involved_after_reading_means_no_block()
    {
        Map(("src/Checkout.cs", "Checkout flow."));
        var chat = new FakeChat("""{"files":[{"path":"src/Checkout.cs","involved":false,"note":""}],"notes":""}""");

        Assert.Null(await Sut(Picks(("src/Checkout.cs", 0.9)), chat).BuildAsync(App, Draft));
    }

    [Fact]
    public async Task Nothing_picked_means_no_block_and_no_chat_call()
    {
        Map(("src/Checkout.cs", "Checkout flow."));
        var chat = new FakeChat("{}");

        Assert.Null(await Sut(Picks(), chat).BuildAsync(App, Draft));
        Assert.Empty(chat.Calls);
    }

    /// <summary>The pick alone is useful to a maintainer; a failed notes call should not throw it away.</summary>
    [Fact]
    public async Task A_failed_notes_call_lists_the_picks_with_their_map_summaries()
    {
        Map(("src/Checkout.cs", "Checkout flow and payment submission."));
        var chat = new FakeChat(new OpenRouterException("down", null, isTransient: true));

        var block = await Sut(Picks(("src/Checkout.cs", 0.9)), chat).BuildAsync(App, Draft);

        Assert.Contains("### Possibly relevant code", block);
        Assert.Contains("— Checkout flow and payment submission.", block);
        Assert.Contains("without reading the files", block);
    }

    [Fact]
    public async Task A_failed_selection_means_no_block()
    {
        Map(("src/Checkout.cs", "Checkout flow."));

        Assert.Null(await Sut(FakeDecisions.Failing(), new FakeChat("{}")).BuildAsync(App, Draft));
    }

    [Fact]
    public async Task Unsummarized_files_are_never_offered()
    {
        Map(("src/Checkout.cs", "Checkout flow."), ("src/Binary.cs", ""));
        var decisions = Picks();

        await Sut(decisions, new FakeChat("{}")).BuildAsync(App, Draft);

        Assert.Equal(1, decisions.Calls.Single().State["files"]!.AsObject().Count);
    }

    [Fact]
    public async Task Paths_are_escaped_in_links()
    {
        Map(("src/My Folder/a(b).cs", "Thing."));
        var chat = new FakeChat("""{"files":[{"path":"src/My Folder/a(b).cs","involved":true,"note":"x"}],"notes":""}""");

        var block = await Sut(Picks(("src/My Folder/a(b).cs", 0.9)), chat).BuildAsync(App, Draft);

        Assert.Contains($"blob/{Commit}/src/My%20Folder/a%28b%29.cs)", block);
    }
}
