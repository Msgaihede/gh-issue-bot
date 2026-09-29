using System.Globalization;
using System.Text;
using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;

namespace DiscordGithubBot.Pipeline;

/// <summary>
/// The report for <c>--dry-run owner/repo "text"</c>: everything the models decided about one report,
/// without saving, posting or creating anything. It exists because the decision thresholds in this bot are
/// pre-probe defaults — run representative reports (a clear bug, a clear feature, an exact duplicate, a
/// near-miss, an off-topic message) and read the raw probabilities that the Debug log prints above this.
/// </summary>
public static class DryRun
{
    public static string Format(ReportAnalysis analysis, string? codeContext, decimal cost, int calls)
    {
        var (type, normalized, review, verdict, openIssues) = analysis;
        var byNumber = openIssues.ToDictionary(i => i.IssueNumber);
        string Issue(int n) => byNumber.TryGetValue(n, out var i) ? $"#{n} {i.Title}" : $"#{n}";

        var sb = new StringBuilder();
        sb.AppendLine($"Type:    {(type == ReportType.Bug ? "Bug report" : "Feature request")}");
        sb.AppendLine("Titles:");
        foreach (var title in normalized.Titles)
            sb.AppendLine($"  {(title == review.Title ? "*" : " ")} {title}");
        sb.AppendLine($"Labels:  {(review.Labels.Count == 0 ? "(none)" : string.Join(", ", review.Labels))}");

        sb.Append($"Dedup:   {verdict.Kind} over {openIssues.Count} open issue(s)");
        if (verdict.IssueNumber is { } match) sb.Append($" -> {Issue(match)}");
        sb.AppendLine();
        if (verdict.CandidateNumbers.Count > 0)
            sb.AppendLine($"  ask about: {string.Join("; ", verdict.CandidateNumbers.Select(Issue))}");
        if (verdict.Shortlist.Count > 0)
            sb.AppendLine($"  shortlist: {string.Join("; ", verdict.Shortlist.Select(Issue))}");

        sb.AppendLine().AppendLine("--- body ---").AppendLine(normalized.Body.Trim());
        sb.AppendLine().AppendLine("--- code context (added on \"Create issue\") ---")
            .AppendLine(string.IsNullOrWhiteSpace(codeContext) ? "(none)" : codeContext.Trim());
        sb.AppendLine().AppendLine(
            $"AI cost: ${cost.ToString("0.#####", CultureInfo.InvariantCulture)} in {calls} call(s). " +
            "Raw decision probabilities are in the Debug lines above.");

        return sb.ToString();
    }
}
