using DiscordGithubBot.CodeContext;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace DiscordGithubBot.Tests.CodeContext;

public sealed class CodeContextFollowUpTests : IDisposable
{
    private static readonly AppConfig App = new() { Name = "MyApp", Repo = "owner/repo" };

    private readonly ICodeContextPrefetcher _prefetcher = Substitute.For<ICodeContextPrefetcher>();
    private readonly IGitHubService _gitHub = Substitute.For<IGitHubService>();
    private readonly ListLogger<CodeContextFollowUp> _logger = new();
    private readonly ServiceProvider _services;
    private readonly CodeContextFollowUp _sut;
    private readonly PendingReport _report = new()
    {
        Id = Guid.NewGuid(), RepoKey = "owner/repo", ReporterDisplayName = "markus", OriginalText = "x",
        DraftTitle = "T", DraftBody = "The body.",
    };

    public CodeContextFollowUpTests()
    {
        _services = new ServiceCollection()
            .AddScoped(_ => _gitHub)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _sut = new CodeContextFollowUp(_prefetcher, _services.GetRequiredService<IServiceScopeFactory>(), _logger);
    }

    public void Dispose() => _services.Dispose();

    /// <summary>What the pipeline hands over: the issue body as it would be composed with a given code context.</summary>
    private static string Compose(string? codeContext) =>
        IssueBodyComposer.ComposeIssueBody("The body.", "markus", "Acme HQ", [], [], codeContext);

    private void SetupCodeContext(string? codeContext) =>
        _prefetcher.GetAsync(_report, App, Arg.Any<CancellationToken>()).Returns(codeContext);

    private void SetupIssueBody(string body) =>
        _gitHub.GetIssueAsync(App, 101, Arg.Any<CancellationToken>())
            .Returns(new GitHubIssue(101, "T", body, "open", DateTime.UtcNow, null, "u"));

    [Fact]
    public async Task The_code_context_is_edited_into_the_issue_body()
    {
        SetupCodeContext("### Relevant code\n- `src/a.cs`");
        SetupIssueBody(Compose(null));

        await _sut.Start(_report, App, 101, Compose);

        await _gitHub.Received(1).UpdateIssueBodyAsync(App, 101, Compose("### Relevant code\n- `src/a.cs`"),
            Arg.Any<CancellationToken>());
        await _gitHub.DidNotReceiveWithAnyArgs().AddCommentAsync(default!, default, default!, default);
    }

    /// <summary>The edit reads the issue as it is now, so an edit made above the marker in the meantime stays.</summary>
    [Fact]
    public async Task An_edit_made_since_the_issue_was_created_is_kept()
    {
        SetupCodeContext("### Relevant code");
        SetupIssueBody(Compose(null).Replace("The body.", "The body, edited."));

        await _sut.Start(_report, App, 101, Compose);

        await _gitHub.Received(1).UpdateIssueBodyAsync(App, 101,
            Arg.Is<string>(b => b.StartsWith("The body, edited.") && b.Contains("### Relevant code")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Nothing_to_add_leaves_the_issue_alone()
    {
        SetupCodeContext(null);

        await _sut.Start(_report, App, 101, Compose);

        Assert.Empty(_gitHub.ReceivedCalls());
    }

    [Fact]
    public async Task A_body_whose_marker_was_removed_is_left_alone()
    {
        SetupCodeContext("### Relevant code");
        SetupIssueBody("Rewritten from scratch by a maintainer.");

        await _sut.Start(_report, App, 101, Compose);

        await _gitHub.DidNotReceiveWithAnyArgs().UpdateIssueBodyAsync(default!, default, default!, default);
        Assert.Contains(_logger.Messages(LogLevel.Warning), m => m.Contains("#101"));
    }

    /// <summary>The issue already exists and the reporter already has their answer: a failed edit is only logged.</summary>
    [Fact]
    public async Task A_failed_edit_is_logged_not_thrown()
    {
        SetupCodeContext("### Relevant code");
        SetupIssueBody(Compose(null));
        _gitHub.UpdateIssueBodyAsync(App, 101, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new HttpRequestException("502"));

        await _sut.Start(_report, App, 101, Compose);

        Assert.Contains(_logger.Messages(LogLevel.Warning), m => m.Contains("#101"));
    }

    [Fact]
    public async Task A_failed_build_is_logged_not_thrown()
    {
        _prefetcher.GetAsync(_report, App, Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new InvalidOperationException("boom"));

        await _sut.Start(_report, App, 101, Compose);

        Assert.Empty(_gitHub.ReceivedCalls());
        Assert.Contains(_logger.Messages(LogLevel.Warning), m => m.Contains("#101"));
    }

    /// <summary>The click must not wait for the build: Start returns while it is still running.</summary>
    [Fact]
    public async Task Start_returns_while_the_build_is_still_running()
    {
        var build = new TaskCompletionSource<string?>();
        _prefetcher.GetAsync(_report, App, Arg.Any<CancellationToken>()).Returns(build.Task);
        SetupIssueBody(Compose(null));

        var following = _sut.Start(_report, App, 101, Compose);

        Assert.False(following.IsCompleted);
        Assert.Empty(_gitHub.ReceivedCalls());

        build.SetResult("### Relevant code");
        await following;
        await _gitHub.Received(1).UpdateIssueBodyAsync(App, 101, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
