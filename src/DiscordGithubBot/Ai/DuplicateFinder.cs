using System.Globalization;
using System.Text.Json.Nodes;
using DiscordGithubBot.Data;
using DiscordGithubBot.OpenRouter;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Ai;

public enum VerdictKind { Match, Uncertain, NoMatch }

/// <param name="IssueNumber">set when Kind == Match</param>
/// <param name="CandidateNumbers">issues worth showing when Kind == Uncertain, most likely first</param>
/// <param name="Shortlist">every issue the first stage kept, most likely first — what the reporter may pick from</param>
public sealed record DuplicateVerdict(
    VerdictKind Kind, int? IssueNumber, IReadOnlyList<int> CandidateNumbers, IReadOnlyList<int> Shortlist);

public interface IDuplicateFinder
{
    /// <summary>
    /// Whether the draft duplicates one of the repo's open issues. No open issues short-circuits to
    /// NoMatch without a call; model failures degrade (see the class remarks) rather than throw.
    /// </summary>
    Task<DuplicateVerdict> FindAsync(IssueDraft draft, IReadOnlyList<CachedIssue> openIssues, CancellationToken ct = default);
}

/// <summary>
/// Finds duplicates by having the decision model read the open issues themselves, in two stages. Stage
/// one is for recall: every open issue — title and the opening of its body — goes past a <c>choice</c>
/// (see <see cref="Shortlist"/>), and the few that draw any real probability survive. Stage two is for
/// precision: each survivor's full excerpt is laid next to the report and scored on three levels —
/// different, related but distinct, the same underlying problem. The probability of the top level is
/// what routes: high enough and the reporter is offered the issue, middling and the reporter is asked,
/// low and it is dropped.
/// </summary>
/// <remarks>
/// Degradation mirrors the old judge's "ask rather than guess": when stage two fails, the reporter is
/// asked about the whole stage-one shortlist. When stage one fails there is nothing to ask about, so the
/// report continues as new — the reporter still confirms the draft before anything is filed.
/// </remarks>
public sealed class DuplicateFinder(IDecisionModel decisions, ILogger<DuplicateFinder> logger) : IDuplicateFinder
{
    /// <summary>
    /// Open issues per stage-one request. ~130 tokens each (title, 300-character excerpt, option), so a full
    /// chunk stays near half of the decision model's 32k-token context.
    /// </summary>
    internal const int ShortlistChunkSize = 120;

    /// <summary>Stage one is for recall: anything the model gives a real chance goes on to be read in full.</summary>
    internal const double ShortlistFloor = 0.05;

    /// <summary>Most issues read side by side in stage two, and the longest pick list a reporter sees.</summary>
    internal const int ShortlistSize = 5;

    private const int ShortlistExcerptChars = 300;
    private const int ReportBodyChars = 4000;

    /// <summary>
    /// P(same) at or above which an issue is offered as the duplicate. Too low offers the reporter an issue
    /// that is not theirs — one click ("Not it") recovers; too high files a duplicate a maintainer merges
    /// later. 0.5 is the pre-probe default for a gate; tune it on <c>--dry-run</c> output.
    /// </summary>
    internal const double MatchProbability = 0.5;

    /// <summary>
    /// P(same) at or above which the reporter is asked. The ask band is the fallback the old judge called
    /// "uncertain": a pick list costs the reporter a glance, a missed duplicate costs a maintainer a merge.
    /// </summary>
    internal const double AskProbability = 0.2;

    /// <summary>Index of the "same" level in <see cref="Levels"/>.</summary>
    private const int SameLevel = 2;

    internal static readonly IReadOnlyList<string> Levels =
    [
        "Different: it is about another problem or request.",
        "Related: it concerns the same feature or similar symptoms, but it is a distinct problem or request " +
        "that would need its own fix or change.",
        "Same: it is the same underlying problem or request; resolving one would resolve the other.",
    ];

    public async Task<DuplicateVerdict> FindAsync(
        IssueDraft draft, IReadOnlyList<CachedIssue> openIssues, CancellationToken ct = default)
    {
        if (openIssues.Count == 0) return NoMatch([]);

        var report = new JsonObject { ["title"] = draft.Title, ["body"] = Truncate(draft.Body, ReportBodyChars) };

        IReadOnlyList<int> shortlist;
        try
        {
            shortlist = await ShortlistAsync(report, openIssues, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Duplicate shortlisting failed over {Count} open issues; treating the report as new.",
                openIssues.Count);
            return NoMatch([]);
        }

        if (shortlist.Count == 0) return NoMatch([]);

        try
        {
            var byNumber = openIssues.ToDictionary(i => i.IssueNumber);
            return await VerifyAsync(report, shortlist.Select(n => byNumber[n]).ToList(), shortlist, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Duplicate verification failed; asking the reporter about {Count} candidates.",
                shortlist.Count);
            return new DuplicateVerdict(VerdictKind.Uncertain, null, shortlist, shortlist);
        }
    }

    private async Task<IReadOnlyList<int>> ShortlistAsync(
        JsonObject report, IReadOnlyList<CachedIssue> openIssues, CancellationToken ct)
    {
        var items = openIssues.Select(i => new ShortlistItem(
                Key(i.IssueNumber),
                $"Issue #{i.IssueNumber} (`issues.{Key(i.IssueNumber)}`) is about the same underlying problem or " +
                "request as `report`.",
                new JsonObject
                {
                    ["number"] = i.IssueNumber,
                    ["title"] = i.Title,
                    ["excerpt"] = Truncate(i.BodyExcerpt, ShortlistExcerptChars),
                }))
            .ToList();

        var picks = await Shortlist.SelectAsync(
            decisions, new JsonObject { ["report"] = report.DeepClone() }, items,
            new ShortlistSpec(
                Purpose: "duplicate_shortlist",
                ItemsField: "issues",
                Instructions:
                "Which existing GitHub issue in `issues`, if any, describes the same underlying problem or request " +
                "as the new report `report`? Judge by what is actually broken or wanted — the same defect or the " +
                "same requested capability — not by shared words or by being about the same part of the app.",
                NoneCriterion: "None of the listed issues is about the same underlying problem or request as `report`.",
                ChunkSize: ShortlistChunkSize, Floor: ShortlistFloor, Take: ShortlistSize),
            ct);

        return picks.Select(p => Number(p.Key)).ToList();
    }

    private async Task<DuplicateVerdict> VerifyAsync(
        JsonObject report, IReadOnlyList<CachedIssue> candidates, IReadOnlyList<int> shortlist, CancellationToken ct)
    {
        var entries = new JsonObject();
        var questions = new Dictionary<string, DecisionQuestion>();

        foreach (var issue in candidates)
        {
            var key = Key(issue.IssueNumber);
            entries[key] = new JsonObject
            {
                ["number"] = issue.IssueNumber,
                ["title"] = issue.Title,
                ["body"] = issue.BodyExcerpt,
            };
            questions[key] = new ScoreQuestion(
                $"How does the existing GitHub issue #{issue.IssueNumber} (`candidates.{key}`) relate to the new " +
                "report `report`? Compare the underlying problem or request each describes, not their wording.",
                Levels);
        }

        var state = new JsonObject { ["report"] = report.DeepClone(), ["candidates"] = entries };
        var result = await decisions.DecideAsync("duplicate_verify", state, questions, ct);

        var same = candidates
            .Select(i => (i.IssueNumber, P: result.Get<ScoreAnswer>(Key(i.IssueNumber)).Probability(SameLevel)))
            .OrderByDescending(x => x.P)
            .ToList();

        var matches = same.Where(x => x.P >= MatchProbability).Select(x => x.IssueNumber).ToList();
        if (matches.Count == 1) return new DuplicateVerdict(VerdictKind.Match, matches[0], [], shortlist);

        // Two issues the model is sure about are two candidates, not a match: the reporter picks.
        var ask = matches.Count > 1
            ? matches
            : same.Where(x => x.P >= AskProbability).Select(x => x.IssueNumber).ToList();

        return ask.Count > 0
            ? new DuplicateVerdict(VerdictKind.Uncertain, null, ask, shortlist)
            : NoMatch(shortlist);
    }

    private static DuplicateVerdict NoMatch(IReadOnlyList<int> shortlist) =>
        new(VerdictKind.NoMatch, null, [], shortlist);

    private static string Key(int issueNumber) => $"issue_{issueNumber.ToString(CultureInfo.InvariantCulture)}";

    private static int Number(string key) => int.Parse(key["issue_".Length..], CultureInfo.InvariantCulture);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
