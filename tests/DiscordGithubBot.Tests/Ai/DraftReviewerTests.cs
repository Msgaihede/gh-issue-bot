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

    /// <summary>
    /// Picks <paramref name="title"/>, names <paramref name="typeLabel"/> as the repo's type label (none when
    /// null), and gives each label question the probability its label name maps to (0 when unmapped).
    /// </summary>
    private static FakeDecisions Answers(string title, Dictionary<string, double> labelP, string? typeLabel = null) =>
        new((_, key, question) => question switch
        {
            ChoiceQuestion when key == "title" => FakeDecisions.Chosen(title),
            ChoiceQuestion q => FakeDecisions.Chosen(
                q.Options.FirstOrDefault(o => typeLabel is not null && o.Criterion.StartsWith($"The label \"{typeLabel}\""))?.Key
                ?? "none"),
            _ => FakeDecisions.Noul(labelP.FirstOrDefault(kv => question.Instructions.Contains($"\"{kv.Key}\"")).Value),
        });

    private static ChoiceQuestion TypeLabelQuestion(FakeDecisions decisions) =>
        decisions.Calls.Single().Questions.Values.OfType<ChoiceQuestion>().Single(q => q.Options.Any(o => o.Key == "none"));

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

    /// <summary>Kind, size and what an issue still needs are independent, so all three can ship together.</summary>
    [Fact]
    public async Task Every_label_that_fits_is_attached_not_just_one()
    {
        RepoLabel[] labels =
        [
            new("bug", "Something isn't working"),
            new("small", "Under a day of work"),
            new("large", "Several days of work"),
            new("clarification needed", "The report is missing information we need to act on it"),
            new("docs", "Documentation"),
        ];
        var decisions = Answers("title_1",
            new() { ["bug"] = 0.9, ["small"] = 0.2, ["large"] = 0.7, ["clarification needed"] = 0.8, ["docs"] = 0.1 },
            typeLabel: "bug");

        var review = await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, labels);

        Assert.Equal(["bug", "large", "clarification needed"], review.Labels);
    }

    /// <summary>Descriptions often carry what the name leaves out, so the model reads both, for every label.</summary>
    [Fact]
    public async Task The_whole_label_set_is_in_state_with_names_and_descriptions()
    {
        var decisions = Answers("title_1", []);

        await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        var call = decisions.Calls.Single();
        var labels = call.State["repository"]!["labels"]!.AsArray();
        Assert.Equal(["bug", "android", "ios"], labels.Select(l => l!["name"]!.GetValue<string>()));
        Assert.Equal("Android app", labels[1]!["description"]!.GetValue<string>());
        Assert.Null(labels[2]!["description"]);
        Assert.Contains(call.Questions.Values.OfType<NoulQuestion>(),
            q => q.Instructions.Contains("\"android\" (`repository.labels[1]`)"));
    }

    /// <summary>
    /// A repository names its type label however it likes; the model finds it by name and description,
    /// and it is attached even when its own yes/no hesitates, so the type and the labels agree.
    /// </summary>
    [Fact]
    public async Task A_custom_named_type_label_the_model_names_is_always_attached()
    {
        RepoLabel[] labels = [new("type: defect", "Something is broken"), new("area: sync", "Cloud sync")];
        var decisions = Answers("title_1", new() { ["type: defect"] = 0.1 }, typeLabel: "type: defect");

        var review = await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, labels);

        Assert.Equal(["type: defect"], review.Labels);
        Assert.True(review.Scores[0].IsTypeLabel);
        var options = TypeLabelQuestion(decisions).Options;
        Assert.Equal(["label_0", "label_1", "none"], options.Select(o => o.Key));
        Assert.Contains("Something is broken", options[0].Criterion);
        Assert.Equal("bug report", decisions.Calls.Single().State["issue"]!["kind"]!.GetValue<string>());
    }

    /// <summary>A conventional name is not enough: the description says what the label is for.</summary>
    [Fact]
    public async Task A_bug_label_is_not_forced_when_the_model_names_no_type_label()
    {
        var decisions = Answers("title_1", new() { ["bug"] = 0.1 });

        var review = await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        Assert.DoesNotContain("bug", review.Labels);
    }

    [Fact]
    public async Task A_torn_type_label_choice_attaches_nothing_by_itself()
    {
        var decisions = new FakeDecisions((_, key, question) => question switch
        {
            ChoiceQuestion when key == "title" => FakeDecisions.Chosen("title_1"),
            ChoiceQuestion => FakeDecisions.Choice(("label_0", 0.45), ("label_1", 0.4), ("none", 0.15)),
            _ => FakeDecisions.Noul(0.1),
        });

        var review = await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        Assert.Empty(review.Labels);
        Assert.DoesNotContain(review.Scores, s => s.IsTypeLabel);
    }

    [Fact]
    public async Task Every_asked_label_is_scored_in_repo_order()
    {
        var decisions = Answers("title_1", new() { ["android"] = 0.9, ["ios"] = 0.3 }, typeLabel: "bug");

        var review = await Sut(decisions).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        Assert.Equal(
            [new LabelScore("bug", 0, true), new LabelScore("android", 0.9, false), new LabelScore("ios", 0.3, false)],
            review.Scores);
    }

    [Fact]
    public async Task A_failed_call_falls_back_to_the_first_title_and_the_conventional_type_label()
    {
        var review = await Sut(FakeDecisions.Failing()).ReviewAsync(App, ReportType.Bug, Draft, Labels);

        Assert.Equal("Save does nothing", review.Title);
        Assert.Equal(["bug"], review.Labels);
        Assert.Empty(review.Scores);
    }

    [Fact]
    public async Task A_failed_call_gives_a_feature_the_repos_enhancement_label_in_the_repos_spelling()
    {
        RepoLabel[] labels = [new("Enhancement", ""), new("bug", "")];

        var review = await Sut(FakeDecisions.Failing()).ReviewAsync(App, ReportType.Feature, Draft, labels);

        Assert.Equal(["Enhancement"], review.Labels);
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
        Assert.Equal(DraftReviewer.MaxLabels + 1, TypeLabelQuestion(decisions).Options.Count);
        Assert.Equal(DraftReviewer.MaxLabels, decisions.Calls.Single().State["repository"]!["labels"]!.AsArray().Count);
    }
}
