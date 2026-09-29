using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Ai;

public class DuplicateFinderTests
{
    private static readonly IssueDraft Draft = new("Save button does nothing on mobile", "Tapping Save has no effect.");

    private static CachedIssue Open(int n, string body = "") => new()
    {
        RepoKey = "owner/repo", IssueNumber = n, Title = $"Issue {n}",
        BodyExcerpt = body.Length > 0 ? body : $"body of {n}", HtmlUrl = $"https://github.com/owner/repo/issues/{n}",
    };

    private static DuplicateFinder Sut(FakeDecisions decisions) => new(decisions, NullLogger<DuplicateFinder>.Instance);

    /// <summary>
    /// Stage one gives <paramref name="shortlist"/> their weights (the rest to "none"); stage two answers each
    /// candidate with P(same) from <paramref name="same"/>, the rest of the mass on "related".
    /// </summary>
    private static FakeDecisions Script(
        (int Issue, double P)[] shortlist, Dictionary<int, double>? same = null) => new((call, key, question) =>
    {
        if (call.Purpose == "duplicate_shortlist")
        {
            var options = ((ChoiceQuestion)question).Options.Select(o => o.Key).ToHashSet();
            var weights = shortlist.Where(s => options.Contains($"issue_{s.Issue}"))
                .Select(s => ($"issue_{s.Issue}", s.P)).ToList();
            return FakeDecisions.Choice(weights.Append((Shortlist.NoneKey, 1 - weights.Sum(w => w.P))).ToArray());
        }

        var number = int.Parse(key["issue_".Length..]);
        var p = same?.GetValueOrDefault(number) ?? 0;
        return FakeDecisions.Score(0, 1 - p, p);
    });

    [Fact]
    public async Task No_open_issues_means_no_match_and_no_call()
    {
        var decisions = Script([]);

        var verdict = await Sut(decisions).FindAsync(Draft, []);

        Assert.Equal(VerdictKind.NoMatch, verdict.Kind);
        Assert.Empty(decisions.Calls);
    }

    /// <summary>When stage one puts its weight on "none", nothing is worth reading in full.</summary>
    [Fact]
    public async Task Nothing_shortlisted_means_no_match_without_a_second_stage()
    {
        var decisions = Script([(1, 0.01)]);

        var verdict = await Sut(decisions).FindAsync(Draft, [Open(1), Open(2)]);

        Assert.Equal(VerdictKind.NoMatch, verdict.Kind);
        Assert.Single(decisions.Calls);
    }

    [Fact]
    public async Task One_issue_that_is_probably_the_same_is_a_match()
    {
        var decisions = Script([(7, 0.8), (9, 0.1)], new() { [7] = 0.85, [9] = 0.05 });

        var verdict = await Sut(decisions).FindAsync(Draft, [Open(7), Open(8), Open(9)]);

        Assert.Equal(VerdictKind.Match, verdict.Kind);
        Assert.Equal(7, verdict.IssueNumber);
        Assert.Equal([7, 9], verdict.Shortlist);
    }

    /// <summary>Stage one reads the title and a short excerpt; stage two reads the whole cached excerpt.</summary>
    [Fact]
    public async Task Stage_two_reads_the_full_excerpt_side_by_side_with_the_report()
    {
        var longBody = new string('a', 2000);
        var decisions = Script([(7, 0.9)], new() { [7] = 0.9 });

        await Sut(decisions).FindAsync(Draft, [Open(7, longBody)]);

        var stageOne = decisions.Calls[0].State;
        Assert.Equal(300, stageOne["issues"]!["issue_7"]!["excerpt"]!.GetValue<string>().Length);

        var stageTwo = decisions.Calls[1];
        Assert.Equal("duplicate_verify", stageTwo.Purpose);
        Assert.Equal(longBody, stageTwo.State["candidates"]!["issue_7"]!["body"]!.GetValue<string>());
        Assert.Equal(Draft.Title, stageTwo.State["report"]!["title"]!.GetValue<string>());
        var score = Assert.IsType<ScoreQuestion>(stageTwo.Questions["issue_7"]);
        Assert.Equal(3, score.Levels.Count);
    }

    [Fact]
    public async Task Two_issues_that_are_both_probably_the_same_are_offered_as_a_pick()
    {
        var decisions = Script([(3, 0.5), (4, 0.4)], new() { [3] = 0.6, [4] = 0.9 });

        var verdict = await Sut(decisions).FindAsync(Draft, [Open(3), Open(4)]);

        Assert.Equal(VerdictKind.Uncertain, verdict.Kind);
        Assert.Equal([4, 3], verdict.CandidateNumbers);
    }

    [Fact]
    public async Task A_middling_probability_asks_the_reporter()
    {
        var decisions = Script([(3, 0.5), (4, 0.4)], new() { [3] = 0.3, [4] = 0.1 });

        var verdict = await Sut(decisions).FindAsync(Draft, [Open(3), Open(4)]);

        Assert.Equal(VerdictKind.Uncertain, verdict.Kind);
        Assert.Equal([3], verdict.CandidateNumbers);
    }

    /// <summary>"Related but distinct" is the model saying no; merely related issues are not offered.</summary>
    [Fact]
    public async Task Related_but_distinct_issues_are_not_offered()
    {
        var decisions = Script([(3, 0.5), (4, 0.4)], new() { [3] = 0.1, [4] = 0.0 });

        var verdict = await Sut(decisions).FindAsync(Draft, [Open(3), Open(4)]);

        Assert.Equal(VerdictKind.NoMatch, verdict.Kind);
        Assert.Equal([3, 4], verdict.Shortlist);
    }

    [Fact]
    public async Task A_failed_first_stage_treats_the_report_as_new()
    {
        var verdict = await Sut(FakeDecisions.Failing()).FindAsync(Draft, [Open(1)]);

        Assert.Equal(VerdictKind.NoMatch, verdict.Kind);
    }

    /// <summary>With a shortlist in hand, a failed verification asks rather than guesses.</summary>
    [Fact]
    public async Task A_failed_second_stage_asks_about_the_whole_shortlist()
    {
        var decisions = new FakeDecisions((call, _, question) => call.Purpose == "duplicate_shortlist"
            ? FakeDecisions.Choice(("issue_1", 0.6), ("issue_2", 0.3), (Shortlist.NoneKey, 0.1))
            : throw new OpenRouterException("down", null, isTransient: true));

        var verdict = await Sut(decisions).FindAsync(Draft, [Open(1), Open(2)]);

        Assert.Equal(VerdictKind.Uncertain, verdict.Kind);
        Assert.Equal([1, 2], verdict.CandidateNumbers);
    }

    [Fact]
    public async Task Many_open_issues_are_read_in_chunks()
    {
        var issues = Enumerable.Range(1, DuplicateFinder.ShortlistChunkSize * 2 + 1).Select(n => Open(n)).ToList();
        var decisions = Script([(5, 0.9)], new() { [5] = 0.9 });

        var verdict = await Sut(decisions).FindAsync(Draft, issues);

        Assert.Equal(3, decisions.Calls.Count(c => c.Purpose == "duplicate_shortlist"));
        Assert.Equal(5, verdict.IssueNumber);
    }
}
