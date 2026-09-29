using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;
using DiscordGithubBot.Pipeline;

namespace DiscordGithubBot.Tests.Pipeline;

public class DryRunTests
{
    private static CachedIssue Open(int n, string title) => new() { RepoKey = "o/r", IssueNumber = n, Title = title };

    private static ReportAnalysis Analysis(DuplicateVerdict verdict) => new(
        ReportType.Bug,
        new NormalizedReport(["Save does nothing", "Save ignored after rotation"], "## Description\nIt broke."),
        new DraftReview("Save ignored after rotation", ["bug", "android"]),
        verdict,
        [Open(7, "Save broken on Android"), Open(9, "Crash on launch")]);

    [Fact]
    public void Shows_type_every_title_with_the_chosen_one_marked_labels_and_body()
    {
        var text = DryRun.Format(Analysis(new DuplicateVerdict(VerdictKind.NoMatch, null, [], [])), null, 0.0031m, 5);

        Assert.Contains("Type:    Bug report", text);
        Assert.Contains("    Save does nothing", text);
        Assert.Contains("  * Save ignored after rotation", text);
        Assert.Contains("Labels:  bug, android", text);
        Assert.Contains("It broke.", text);
        Assert.Contains("--- code context (added on \"Create issue\") ---\n(none)", text.Replace("\r\n", "\n"));
        Assert.Contains("AI cost: $0.0031 in 5 call(s)", text);
    }

    [Fact]
    public void Names_the_matched_and_shortlisted_issues()
    {
        var text = DryRun.Format(
            Analysis(new DuplicateVerdict(VerdictKind.Match, 7, [], [7, 9])), "### Relevant code", 0m, 0);

        Assert.Contains("Dedup:   Match over 2 open issue(s) -> #7 Save broken on Android", text);
        Assert.Contains("shortlist: #7 Save broken on Android; #9 Crash on launch", text);
        Assert.Contains("### Relevant code", text);
    }
}
