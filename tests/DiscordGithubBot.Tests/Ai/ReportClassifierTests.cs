using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Ai;

public class ReportClassifierTests
{
    private static ReportClassifier Sut(FakeDecisions decisions) =>
        new(decisions, NullLogger<ReportClassifier>.Instance);

    [Theory]
    [InlineData("bug", ReportType.Bug)]
    [InlineData("feature", ReportType.Feature)]
    public async Task The_models_choice_decides_the_type(string choice, ReportType expected)
    {
        var decisions = new FakeDecisions((_, _, _) => FakeDecisions.Chosen(choice));

        Assert.Equal(expected, await Sut(decisions).ClassifyAsync("MyApp", "text"));
    }

    [Fact]
    public async Task One_choice_between_bug_and_feature_over_the_app_and_report()
    {
        var decisions = new FakeDecisions((_, _, _) => FakeDecisions.Chosen("bug"));

        await Sut(decisions).ClassifyAsync("MyApp", "The save button does nothing");

        var call = Assert.Single(decisions.Calls);
        Assert.Equal("MyApp", call.State["app"]!.GetValue<string>());
        Assert.Equal("The save button does nothing", call.State["report"]!.GetValue<string>());
        var question = Assert.IsType<ChoiceQuestion>(Assert.Single(call.Questions).Value);
        Assert.Equal(["bug", "feature"], question.Options.Select(o => o.Key));
    }

    [Fact]
    public async Task A_long_report_is_cut_before_it_reaches_the_model()
    {
        var decisions = new FakeDecisions((_, _, _) => FakeDecisions.Chosen("bug"));

        await Sut(decisions).ClassifyAsync("MyApp", new string('x', 10_000));

        Assert.Equal(4000, decisions.Calls.Single().State["report"]!.GetValue<string>().Length);
    }

    /// <summary>Most reports are bugs, and the preview shows the type before anything is filed.</summary>
    [Fact]
    public async Task A_failed_call_falls_back_to_bug()
    {
        Assert.Equal(ReportType.Bug, await Sut(FakeDecisions.Failing()).ClassifyAsync("MyApp", "text"));
    }

    [Fact]
    public async Task Cancellation_of_our_own_token_still_propagates()
    {
        using var cts = new CancellationTokenSource();
        var decisions = new FakeDecisions((_, _, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sut(decisions).ClassifyAsync("MyApp", "text", cts.Token));
    }
}
