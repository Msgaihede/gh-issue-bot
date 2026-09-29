using DiscordGithubBot.Ai;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Ai;

public class DraftReviewerTests
{
    private static readonly AppConfig App = new() { Name = "MyApp", Repo = "owner/repo" };

    private static readonly NormalizedReport Draft = new(
        ["Save does nothing", "Save button ignored on Android after rotating", "Saving fails"],
        "## Description\nTapping Save on Android after rotating the phone does nothing.");

    private static readonly RepoLabel[] Labels =
    [
        new("bug", "Something isn't working"),
        new("android", "Android app"),
        new("ios", ""),
        new("duplicate", "This issue or pull request already exists"),
        new("good first issue", "Good for newcomers"),
    ];

    private static DraftReviewer Sut(FakeDecisions decisions) => new(decisions, NullLogger<DraftReviewer>.Instance);

    /// <summary>Picks <paramref name="title"/> and gives each label question the probability its label name maps to.</summary>
    private static FakeDecisions Answers(string title, Dictionary<string, double> labelP) => new((_, key, question) =>
        key == "title"
            ? FakeDecisions.Chosen(title)
            : FakeDecisions.Noul(labelP.FirstOrDefault(kv => question.Instructions.Contains($"\"{kv.Key}\"")).Value));

    [Fact]
    public async Task The_chosen_title_ships()
    {
        var review = await Sut(Answers("title_2", [])).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        Assert.Equal("Save button ignored on Android after rotating", review.Title);
    }

    [Fact]
    public async Task Titles_are_offered_as_options_that_quote_them_and_the_body_is_in_state()
    {
        var decisions = Answers("title_1", []);

        await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        var call = Assert.Single(decisions.Calls);
        var title = Assert.IsType<ChoiceQuestion>(call.Questions["title"]);
        Assert.Equal(["title_1", "title_2", "title_3"], title.Options.Select(o => o.Key));
        Assert.Contains("\"Saving fails\"", title.Options[2].Criterion);
        Assert.Contains("rotating the phone", call.State["issue"]!["body"]!.GetValue<string>());
        Assert.Equal("bug report", call.State["issue"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Labels_at_or_above_the_threshold_are_attached_in_repo_order()
    {
        var decisions = Answers("title_1", new() { ["android"] = 0.9, ["ios"] = 0.49, ["bug"] = 0.95 });

        var review = await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        Assert.Equal(["bug", "android"], review.Labels);
    }

    [Fact]
    public async Task Each_label_is_its_own_yes_no_question_carrying_its_description()
    {
        var decisions = Answers("title_1", []);

        await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        var nouls = decisions.Calls.Single().Questions.Values.OfType<NoulQuestion>().ToList();
        Assert.Equal(3, nouls.Count);
        Assert.Contains(nouls, q => q.Instructions.Contains("\"android\"") && q.Instructions.Contains("Android app"));
        Assert.Contains(nouls, q => q.Instructions.Contains("\"ios\"") && q.Instructions.Contains("no description"));
    }

    /// <summary>Triage outcomes are the maintainer's call, never the bot's.</summary>
    [Fact]
    public async Task Triage_labels_are_never_offered()
    {
        var decisions = Answers("title_1", new() { ["duplicate"] = 1.0, ["good first issue"] = 1.0 });

        var review = await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        Assert.DoesNotContain("duplicate", review.Labels);
        Assert.DoesNotContain("good first issue", review.Labels);
        Assert.DoesNotContain(decisions.Calls.Single().Questions.Values, q => q.Instructions.Contains("\"duplicate\""));
    }

    [Fact]
    public async Task A_configured_ignore_list_replaces_the_defaults()
    {
        var app = new AppConfig { Name = "MyApp", Repo = "owner/repo", IgnoredLabels = ["android"] };
        var decisions = Answers("title_1", new() { ["android"] = 1.0, ["duplicate"] = 1.0 });

        var review = await Sut(decisions).ReviewAsync(app, ReportType.Bug, Draft, Labels);

        Assert.DoesNotContain("android", review.Labels);
        Assert.Contains("duplicate", review.Labels);
    }

    /// <summary>The classified type and the labels must agree on the basics even when the model hesitates.</summary>
    [Fact]
    public async Task The_repos_type_label_is_always_attached()
    {
        var decisions = Answers("title_1", new() { ["bug"] = 0.1 });

        var review = await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        Assert.Contains("bug", review.Labels);
    }

    [Fact]
    public async Task A_feature_gets_the_repos_enhancement_label_in_the_repos_spelling()
    {
        RepoLabel[] labels = [new("Enhancement", ""), new("bug", "")];

        var review = await Sut(Answers("title_1", [])).ReviewAsync(App, ReportType.Feature, Draft, labels);

        Assert.Equal(["Enhancement"], review.Labels);
    }

    [Fact]
    public async Task A_failed_call_falls_back_to_the_first_title_and_the_type_label()
    {
        var review = await Sut(FakeDecisions.Failing()).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        Assert.Equal("Save does nothing", review.Title);
        Assert.Equal(["bug"], review.Labels);
    }

    [Fact]
    public async Task A_choice_outside_the_offered_titles_falls_back_to_the_first()
    {
        var review = await Sut(Answers("title_9", [])).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        Assert.Equal("Save does nothing", review.Title);
    }

    [Fact]
    public async Task One_title_and_no_labels_means_no_call()
    {
        var decisions = Answers("title_1", []);

        var review = await Sut(decisions).ReviewAsync(App, ReportType.Bug, new NormalizedReport(["Only"], "b"), []);

        Assert.Equal("Only", review.Title);
        Assert.Empty(review.Labels);
        Assert.Empty(decisions.Calls);
    }

    [Fact]
    public async Task Labels_beyond_the_cap_are_not_asked_about()
    {
        var many = Enumerable.Range(0, DraftReviewer.MaxLabels + 20).Select(i => new RepoLabel($"l{i}", "")).ToArray();
        var decisions = Answers("title_1", []);

        await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, many);

        Assert.Equal(DraftReviewer.MaxLabels, decisions.Calls.Single().Questions.Values.OfType<NoulQuestion>().Count());
    }
}
