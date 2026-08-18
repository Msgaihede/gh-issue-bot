using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.Ai;

public class DuplicateJudgeTests
{
    private static IssueEmbedding Candidate(int n, string state = "open") => new()
    {
        RepoKey = "o/r", IssueNumber = n, Title = $"Issue {n}", State = state,
        ContentHash = "h", BodyExcerpt = $"body {n}",
    };

    private static DuplicateJudge Sut(FakeChatClient chat) => new(chat, NullLogger<DuplicateJudge>.Instance);
    private static readonly IssueDraft Draft = new("T", "B");

    [Fact]
    public async Task Match_verdict_maps_to_match()
    {
        var chat = new FakeChatClient("""{"verdict":"match","issueNumber":7}""");
        var v = await Sut(chat).JudgeAsync(Draft, [Candidate(7), Candidate(9)]);
        Assert.Equal(VerdictKind.Match, v.Kind);
        Assert.Equal(7, v.IssueNumber);
    }

    [Fact]
    public async Task Match_with_unknown_issue_number_degrades_to_uncertain()
    {
        var chat = new FakeChatClient("""{"verdict":"match","issueNumber":999}""");
        var v = await Sut(chat).JudgeAsync(Draft, [Candidate(7), Candidate(9)]);
        Assert.Equal(VerdictKind.Uncertain, v.Kind);
        Assert.Equal([7, 9], v.CandidateNumbers);
    }

    [Fact]
    public async Task Uncertain_intersects_candidates()
    {
        var chat = new FakeChatClient("""{"verdict":"uncertain","candidates":[9,999]}""");
        var v = await Sut(chat).JudgeAsync(Draft, [Candidate(7), Candidate(9)]);
        Assert.Equal(VerdictKind.Uncertain, v.Kind);
        Assert.Equal([9], v.CandidateNumbers);
    }

    [Fact]
    public async Task No_match_maps_to_nomatch()
    {
        var chat = new FakeChatClient("""{"verdict":"no_match"}""");
        var v = await Sut(chat).JudgeAsync(Draft, [Candidate(7)]);
        Assert.Equal(VerdictKind.NoMatch, v.Kind);
    }

    [Fact]
    public async Task Garbage_degrades_to_uncertain_over_all()
    {
        var chat = new FakeChatClient("garbage");
        var v = await Sut(chat).JudgeAsync(Draft, [Candidate(7), Candidate(9)]);
        Assert.Equal(VerdictKind.Uncertain, v.Kind);
        Assert.Equal([7, 9], v.CandidateNumbers);
    }

    [Fact]
    public async Task Empty_candidates_short_circuits_without_llm_call()
    {
        var chat = new FakeChatClient("should never be used");
        var v = await Sut(chat).JudgeAsync(Draft, []);
        Assert.Equal(VerdictKind.NoMatch, v.Kind);
        Assert.Empty(chat.Prompts);
    }

    [Fact]
    public async Task Candidate_body_excerpts_reach_the_prompt()
    {
        var chat = new FakeChatClient("""{"verdict":"no_match"}""");
        await Sut(chat).JudgeAsync(Draft, [Candidate(7)]);
        Assert.Contains("body 7", chat.Prompts[0]);
        Assert.Contains("#7", chat.Prompts[0]);
    }
}
