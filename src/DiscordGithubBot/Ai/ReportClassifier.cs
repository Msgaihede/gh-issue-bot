using System.Text.Json.Nodes;
using DiscordGithubBot.Data;
using DiscordGithubBot.OpenRouter;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Ai;

public interface IReportClassifier
{
    /// <summary>Bug or feature, decided by the decision model. Never throws on a model failure — falls back to <see cref="ReportType.Bug"/>.</summary>
    Task<ReportType> ClassifyAsync(string appName, string rawText, CancellationToken ct = default);
}

/// <summary>
/// Decides what kind of issue a report is, which picks the draft template and the canonical type label.
/// There is one command for every report now, so this is the question the reporter used to answer by
/// choosing between two commands. A failed call falls back to bug: most reports are, and the reporter sees
/// the type on the preview before anything reaches GitHub.
/// </summary>
public sealed class ReportClassifier(IDecisionModel decisions, ILogger<ReportClassifier> logger) : IReportClassifier
{
    internal const string QuestionKey = "type";

    /// <summary>Longest report handed to the model; the modal already caps input well below this.</summary>
    private const int MaxReportChars = 4000;

    internal static readonly ChoiceQuestion Question = new(
        "What kind of GitHub issue is `report`, written by a user of the app named in `app`? Judge by what the " +
        "user needs: something that should already work to be fixed, or something new or different to be built.",
        [
            new("bug", "Something in the app is broken or wrong: it crashes, errors, loses data, looks broken, or " +
                       "behaves differently from how it is meant to work, and the user wants that fixed."),
            new("feature", "The app works as designed and the user asks for new functionality, a change to how an " +
                           "existing feature behaves, or an improvement."),
        ]);

    public async Task<ReportType> ClassifyAsync(string appName, string rawText, CancellationToken ct = default)
    {
        var state = new JsonObject
        {
            ["app"] = appName,
            ["report"] = rawText.Length <= MaxReportChars ? rawText : rawText[..MaxReportChars],
        };

        try
        {
            var result = await decisions.DecideAsync(
                "report_type", state, new Dictionary<string, DecisionQuestion> { [QuestionKey] = Question }, ct);
            var answer = result.Get<ChoiceAnswer>(QuestionKey);

            return answer.Choice == "feature" ? ReportType.Feature : ReportType.Bug;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Classifying a report for {App} failed; treating it as a bug.", appName);
            return ReportType.Bug;
        }
    }
}
