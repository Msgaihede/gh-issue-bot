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
public sealed record DraftReview(string Title, IReadOnlyList<string> Labels);

public interface IDraftReviewer
{
    /// <summary>Chooses the title and the labels. Never throws on a model failure — degrades to the first title and the type label.</summary>
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
/// Labels are asked one <c>noul</c> each, because several can apply at once and each is an independent
/// yes/no. Labels that record a maintainer's triage outcome rather than what the issue is about are never
/// offered (<see cref="DefaultIgnoredLabels"/>, overridable per app). The repository's canonical type
/// label is always attached when it exists, so the classified type and the labels cannot disagree about
/// the basics even when the model is unsure.
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

    /// <summary>Most labels asked about; ~70 tokens each keeps a request well inside the model's context.</summary>
    internal const int MaxLabels = 100;

    private const int MaxBodyChars = 4000;
    private const string TitleKey = "title";

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

        var typeLabel = TypeLabel(type, eligible);
        var fallback = new DraftReview(draft.Titles[0], typeLabel is null ? [] : [typeLabel.Name]);

        var questions = new Dictionary<string, DecisionQuestion>();
        if (draft.Titles.Count > 1) questions[TitleKey] = TitleQuestion(draft.Titles);
        for (var i = 0; i < eligible.Count; i++) questions[LabelKey(i)] = LabelQuestion(eligible[i]);
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

        try
        {
            var result = await decisions.DecideAsync("draft_review", state, questions, ct);

            var index = draft.Titles.Count > 1 ? TitleIndex(result.Get<ChoiceAnswer>(TitleKey).Choice) : 0;
            var title = index >= 0 && index < draft.Titles.Count ? draft.Titles[index] : draft.Titles[0];

            var labels = eligible
                .Where((label, i) => label == typeLabel || result.Get<NoulAnswer>(LabelKey(i)).Probability >= LabelProbability)
                .Select(l => l.Name)
                .ToList();

            return new DraftReview(title, labels);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reviewing the draft for {Repo} failed; using the first title and the type label.", app.Repo);
            return fallback;
        }
    }

    private static ChoiceQuestion TitleQuestion(IReadOnlyList<string> titles) => new(
        "Which title most accurately and specifically describes the actual problem or request in `issue.body`, " +
        "so that a maintainer scanning a list of issue titles would understand what this issue is about " +
        "without opening it? Prefer the title that names the concrete symptom or capability and where it " +
        "happens; a vague or generic title is worse even if it is shorter.",
        titles.Select((t, i) => new ChoiceOption(TitleOption(i), $"The title \"{t}\".")).ToList());

    private static NoulQuestion LabelQuestion(RepoLabel label)
    {
        var meaning = string.IsNullOrWhiteSpace(label.Description)
            ? "The label has no description; judge it by its name."
            : $"The label is described as: {label.Description.Trim()}";

        return new NoulQuestion(
            $"Does the repository's GitHub label \"{label.Name}\" apply to the issue in `issue`? {meaning}",
            IfTrue: $"The issue squarely falls under what \"{label.Name}\" marks.",
            IfFalse: $"\"{label.Name}\" does not fit this issue, or fits it only loosely.");
    }

    /// <summary>The repository's own label for the classified type, when it has one under a conventional name.</summary>
    private static RepoLabel? TypeLabel(ReportType type, IReadOnlyList<RepoLabel> eligible)
    {
        var names = type == ReportType.Bug ? BugLabelNames : FeatureLabelNames;
        return names
            .Select(n => eligible.FirstOrDefault(l => string.Equals(l.Name, n, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(l => l is not null);
    }

    private static string LabelKey(int index) => $"label_{index.ToString(CultureInfo.InvariantCulture)}";

    private static string TitleOption(int index) => $"title_{(index + 1).ToString(CultureInfo.InvariantCulture)}";

    /// <returns>The zero-based index the option names, or -1 for anything that is not a title option.</returns>
    private static int TitleIndex(string option) =>
        option.StartsWith("title_", StringComparison.Ordinal)
        && int.TryParse(option.AsSpan("title_".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n - 1
            : -1;
}
