using System.Globalization;
using System.Text.Json.Nodes;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.OpenRouter;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Ai;

/// <param name="Title">the title the issue ships with</param>
/// <param name="Labels">repository labels to attach, in the repository's own order and spelling</param>
public sealed record DraftReview(string Title, IReadOnlyList<string> Labels)
{
    /// <summary>
    /// Every label the model was asked about, in repository order, with its answer; empty when no call was
    /// made or it failed. What <c>--dry-run</c> prints, since the raw answers are keyed by index.
    /// </summary>
    public IReadOnlyList<LabelScore> Scores { get; init; } = [];
}

/// <param name="Probability">P(the label applies to the issue)</param>
/// <param name="IsTypeLabel">the model named it as the label the repository marks this kind of issue with,
/// which attaches it whatever <paramref name="Probability"/> says</param>
public sealed record LabelScore(string Name, double Probability, bool IsTypeLabel);

public interface IDraftReviewer
{
    /// <summary>Chooses the title and the labels. Never throws on a model failure — degrades to the first title and the conventional type label.</summary>
    Task<DraftReview> ReviewAsync(
        AppConfig app, ReportType type, NormalizedReport draft, IReadOnlyList<RepoLabel> repoLabels,
        CancellationToken ct = default);
}

/// <summary>
/// The decision model's pass over a finished draft, in one request: which of the alternative titles
/// describes the issue best, and which of the repository's own labels apply. Both are selection, not
/// writing — the titles come from the draft model and the labels from the repository — which is what a
/// decision model is for: it cannot invent a title nobody wrote or a label the repo does not have.
/// </summary>
/// <remarks>
/// Nothing here knows any repository's label scheme: every label is judged by its name and its
/// description, whatever the repository calls it. The whole label set sits in state, so a label is read
/// next to the ones it is meant to be told apart from (<c>small</c>/<c>medium</c>/<c>large</c>,
/// <c>android</c>/<c>ios</c>). Labels are asked one <c>noul</c> each, because several can apply at once —
/// an issue can be <c>bug</c>, <c>large</c> and <c>clarification needed</c> together — and each is an
/// independent yes/no. One <c>choice</c> in the same request names the label the repository marks the
/// classified kind of issue with, and that label is always attached, so the type and the labels cannot
/// disagree about the basics even when the label's own answer hesitates. Labels that record a
/// maintainer's triage outcome are never offered (<see cref="DefaultIgnoredLabels"/>, overridable per app).
/// </remarks>
public sealed class DraftReviewer(IDecisionModel decisions, ILogger<DraftReviewer> logger) : IDraftReviewer
{
    /// <summary>Triage outcomes a maintainer decides, not properties of a fresh report.</summary>
    public static readonly IReadOnlyList<string> DefaultIgnoredLabels =
        ["duplicate", "invalid", "wontfix", "won't fix", "good first issue", "help wanted"];

    /// <summary>
    /// P(applies) at or above which a label is attached. A wrong label mis-files the issue in every
    /// maintainer view filtered by it; a missed one is added by hand at triage. 0.5 is the pre-probe
    /// default for a gate — tune it on <c>--dry-run</c> output.
    /// </summary>
    internal const double LabelProbability = 0.5;

    /// <summary>
    /// Probability the type-label choice must give its pick before that label is attached unconditionally.
    /// Below it the model is torn between labels (or between a label and none), and the pick's own
    /// <c>noul</c> decides like any other label's. Pre-probe default.
    /// </summary>
    internal const double TypeLabelProbability = 0.5;

    /// <summary>Most labels asked about — twice the largest label set expected — so a request stays small.</summary>
    internal const int MaxLabels = 100;

    private const int MaxBodyChars = 4000;
    private const string TitleKey = "title";
    private const string TypeLabelKey = "type_label";
    private const string NoTypeLabel = "none";

    /// <summary>Names GitHub's default labels use; the type label only when the model could not be asked.</summary>
    private static readonly IReadOnlyList<string> BugLabelNames = ["bug"];
    private static readonly IReadOnlyList<string> FeatureLabelNames = ["enhancement", "feature", "feature request", "feature-request"];

    public async Task<DraftReview> ReviewAsync(
        AppConfig app, ReportType type, NormalizedReport draft, IReadOnlyList<RepoLabel> repoLabels,
        CancellationToken ct = default)
    {
        var ignored = new HashSet<string>(app.IgnoredLabels ?? DefaultIgnoredLabels, StringComparer.OrdinalIgnoreCase);
        var eligible = repoLabels.Where(l => !ignored.Contains(l.Name)).ToList();
        if (eligible.Count > MaxLabels)
        {
            logger.LogWarning("{Repo} has {Count} labels; only the first {Max} are considered.", app.Repo, eligible.Count, MaxLabels);
            eligible = eligible.Take(MaxLabels).ToList();
        }

        var conventional = ConventionalTypeLabel(type, eligible);
        var fallback = new DraftReview(draft.Titles[0], conventional is null ? [] : [conventional.Name]);

        var questions = new Dictionary<string, DecisionQuestion>();
        if (draft.Titles.Count > 1) questions[TitleKey] = TitleQuestion(draft.Titles);
        if (eligible.Count > 0) questions[TypeLabelKey] = TypeLabelQuestion(eligible);
        for (var i = 0; i < eligible.Count; i++) questions[LabelKey(i)] = LabelQuestion(eligible[i], i);
        if (questions.Count == 0) return fallback;

        var state = new JsonObject
        {
            ["app"] = app.Name,
            ["issue"] = new JsonObject
            {
                ["kind"] = type == ReportType.Bug ? "bug report" : "feature request",
                ["title"] = draft.Titles[0],
                ["body"] = draft.Body.Length <= MaxBodyChars ? draft.Body : draft.Body[..MaxBodyChars],
            },
        };
        if (eligible.Count > 0) state["repository"] = new JsonObject { ["labels"] = LabelList(eligible) };

        try
        {
            var result = await decisions.DecideAsync("draft_review", state, questions, ct);

            var index = draft.Titles.Count > 1 ? TitleIndex(result.Get<ChoiceAnswer>(TitleKey).Choice) : 0;
            var title = index >= 0 && index < draft.Titles.Count ? draft.Titles[index] : draft.Titles[0];

            var typeIndex = eligible.Count > 0 ? TypeLabelIndex(result.Get<ChoiceAnswer>(TypeLabelKey)) : -1;
            var scores = eligible
                .Select((label, i) => new LabelScore(
                    label.Name, result.Get<NoulAnswer>(LabelKey(i)).Probability, i == typeIndex))
                .ToList();
            var labels = scores
                .Where(s => s.IsTypeLabel || s.Probability >= LabelProbability)
                .Select(s => s.Name)
                .ToList();

            return new DraftReview(title, labels) { Scores = scores };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reviewing the draft for {Repo} failed; using the first title and the conventional type label.", app.Repo);
            return fallback;
        }
    }

    private static ChoiceQuestion TitleQuestion(IReadOnlyList<string> titles) => new(
        "Which title most accurately and specifically describes the actual problem or request in `issue.body`, " +
        "so that a maintainer scanning a list of issue titles would understand what this issue is about " +
        "without opening it? Prefer the title that names the concrete symptom or capability and where it " +
        "happens; a vague or generic title is worse even if it is shorter.",
        titles.Select((t, i) => new ChoiceOption(TitleOption(i), $"The title \"{t}\".")).ToList());

    /// <summary>
    /// Which label means "this is a bug" (or "a feature request") in this repository. Asked of the label
    /// set rather than looked up by name, because repositories call it anything — <c>bug</c>,
    /// <c>type: bug</c>, <c>kind/defect</c> — and only the description may say which one it is.
    /// </summary>
    private static ChoiceQuestion TypeLabelQuestion(IReadOnlyList<RepoLabel> labels) => new(
        "Which one of the repository's labels in `repository.labels` does this repository use to mark an issue " +
        "as the kind given in `issue.kind`? That label says what kind of issue it is, such as a bug or a feature " +
        "request — not which part of the app it concerns, how big it is, or what state it is in. Judge each " +
        "label by its name and its description.",
        [
            .. labels.Select((l, i) => new ChoiceOption(LabelKey(i), $"The label \"{l.Name}\". {Meaning(l)}")),
            new ChoiceOption(NoTypeLabel,
                "None of the labels marks this kind of issue; they mark other things, such as the area, the " +
                "platform, the size or the status."),
        ]);

    private static NoulQuestion LabelQuestion(RepoLabel label, int index) => new(
        $"Does the repository's GitHub label \"{label.Name}\" " +
        $"(`repository.labels[{index.ToString(CultureInfo.InvariantCulture)}]`) apply to the issue in `issue`? " +
        $"{Meaning(label)} Labels are not exclusive — an issue can carry several at once, such as its kind, its " +
        "size and what it still needs — so judge this one on its own; the other labels in `repository.labels` " +
        "show what it is meant to be told apart from.",
        IfTrue: $"The issue squarely falls under what \"{label.Name}\" marks.",
        IfFalse: $"\"{label.Name}\" does not fit this issue, or fits it only loosely.");

    /// <summary>What the label means: its description, or a note that it has none and its name must do.</summary>
    private static string Meaning(RepoLabel label)
    {
        if (string.IsNullOrWhiteSpace(label.Description)) return "The label has no description; judge it by its name.";

        // Descriptions are usually fragments ("Android app"); a stop keeps them from running into what follows.
        var description = label.Description.Trim();
        return $"The label is described as: {description}{(description[^1] is '.' or '!' or '?' ? "" : ".")}";
    }

    /// <summary>The label set as state, in the order the questions index it; a missing description is left out.</summary>
    private static JsonArray LabelList(IReadOnlyList<RepoLabel> labels) =>
    [
        .. labels.Select(l => (JsonNode)(string.IsNullOrWhiteSpace(l.Description)
            ? new JsonObject { ["name"] = l.Name }
            : new JsonObject { ["name"] = l.Name, ["description"] = l.Description.Trim() })),
    ];

    /// <summary>
    /// The repository's label for the classified type under a name GitHub's defaults use. Only the fallback
    /// when the model cannot be asked: with a model answer, the type label is whichever label it names.
    /// </summary>
    private static RepoLabel? ConventionalTypeLabel(ReportType type, IReadOnlyList<RepoLabel> eligible)
    {
        var names = type == ReportType.Bug ? BugLabelNames : FeatureLabelNames;
        return names
            .Select(n => eligible.FirstOrDefault(l => string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(l => l is not null);
    }

    /// <returns>The index of the label the choice names with enough probability, or -1 for none.</returns>
    private static int TypeLabelIndex(ChoiceAnswer answer) =>
        answer.Probability(answer.Choice) >= TypeLabelProbability ? OptionNumber(answer.Choice, "label_") : -1;

    private static string LabelKey(int index) => $"label_{index.ToString(CultureInfo.InvariantCulture)}";

    private static string TitleOption(int index) => $"title_{(index + 1).ToString(CultureInfo.InvariantCulture)}";

    /// <returns>The zero-based index the option names, or -1 for anything that is not a title option.</returns>
    private static int TitleIndex(string option) => OptionNumber(option, "title_") is var n and > 0 ? n - 1 : -1;

    /// <returns>The number after <paramref name="prefix"/>, or -1 when the option does not start with it.</returns>
    private static int OptionNumber(string option, string prefix) =>
        option.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(option.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : -1;
}
