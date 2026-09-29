using DiscordGithubBot.Ai;
using DiscordGithubBot.CodeContext;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordGithubBot.Tests.CodeContext;

public sealed class CodeContextPrefetcherTests : IDisposable
{
    private static readonly AppConfig App = new() { Name = "MyApp", Repo = "owner/repo" };
    private static readonly IssueDraft Draft = new("Search ignores apostrophes", "Searching for Urza's Saga finds nothing.");

    private readonly ICodeContextBuilder _builder = Substitute.For<ICodeContextBuilder>();
    private readonly IPendingReportStore _store = Substitute.For<IPendingReportStore>();
    private readonly ServiceProvider _services;
    private readonly CodeContextPrefetcher _sut;

    public CodeContextPrefetcherTests()
    {
        _services = new ServiceCollection()
            .AddScoped(_ => _builder)
            .AddScoped(_ => _store)
            .AddScoped<AiUsageMeter>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _sut = new CodeContextPrefetcher(_services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CodeContextPrefetcher>.Instance);
    }

    public void Dispose() => _services.Dispose();

    private static PendingReport Report(Guid id, string? codeContext = null, DateTime? readyAt = null) => new()
    {
        Id = id, RepoKey = "owner/repo", ReporterDisplayName = "u", OriginalText = "x",
        DraftTitle = Draft.Title, DraftBody = Draft.Body, CodeContext = codeContext, CodeContextReadyAtUtc = readyAt,
    };

    [Fact]
    public async Task A_background_build_is_stored_and_handed_to_the_confirmation()
    {
        var id = Guid.NewGuid();
        _builder.BuildAsync(App, Draft, Arg.Any<CancellationToken>()).Returns("### Relevant code");

        _sut.Start(id, App, Draft);
        var result = await _sut.GetAsync(Report(id), App);

        Assert.Equal("### Relevant code", result);
        await _store.Received(1).SetCodeContextAsync(id, "### Relevant code", Arg.Any<CancellationToken>());
        await _builder.Received(1).BuildAsync(App, Draft, Arg.Any<CancellationToken>());
    }

    /// <summary>A confirmation that lands mid-build waits for that build rather than starting a second one.</summary>
    [Fact]
    public async Task A_confirmation_during_the_build_waits_for_it()
    {
        var id = Guid.NewGuid();
        var release = new TaskCompletionSource<string?>();
        _builder.BuildAsync(App, Draft, Arg.Any<CancellationToken>()).Returns(release.Task);

        _sut.Start(id, App, Draft);
        var waiting = _sut.GetAsync(Report(id), App);
        Assert.False(waiting.IsCompleted);

        release.SetResult("### Relevant code");
        Assert.Equal("### Relevant code", await waiting);
        await _builder.Received(1).BuildAsync(App, Draft, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stored_result_is_used_without_building()
    {
        var result = await _sut.GetAsync(Report(Guid.NewGuid(), "### Stored", DateTime.UtcNow), App);

        Assert.Equal("### Stored", result);
        await _builder.DidNotReceiveWithAnyArgs().BuildAsync(default!, default!, default);
    }

    /// <summary>"" is a finished build that found nothing: no block, and no second build either.</summary>
    [Fact]
    public async Task A_stored_empty_result_means_no_block()
    {
        Assert.Null(await _sut.GetAsync(Report(Guid.NewGuid(), "", DateTime.UtcNow), App));
        await _builder.DidNotReceiveWithAnyArgs().BuildAsync(default!, default!, default);
    }

    /// <summary>After a restart nothing is running and nothing was stored: the confirmation builds it once.</summary>
    [Fact]
    public async Task With_nothing_stored_or_running_the_confirmation_builds_it()
    {
        _builder.BuildAsync(App, Draft, Arg.Any<CancellationToken>()).Returns("### Built now");

        Assert.Equal("### Built now", await _sut.GetAsync(Report(Guid.NewGuid()), App));
    }

    [Fact]
    public async Task A_build_that_finds_nothing_is_stored_as_empty()
    {
        var id = Guid.NewGuid();
        _builder.BuildAsync(App, Draft, Arg.Any<CancellationToken>()).Returns((string?)null);

        _sut.Start(id, App, Draft);

        Assert.Null(await _sut.GetAsync(Report(id), App));
        await _store.Received(1).SetCodeContextAsync(id, "", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_background_build_means_no_block_rather_than_a_failed_issue()
    {
        var id = Guid.NewGuid();
        _builder.BuildAsync(App, Draft, Arg.Any<CancellationToken>()).Returns<string?>(_ => throw new InvalidOperationException("boom"));

        _sut.Start(id, App, Draft);

        Assert.Null(await _sut.GetAsync(Report(id), App));
    }
}
