using System.Text.Json.Nodes;
using DiscordGithubBot.Ai;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Tests.TestDoubles;

namespace DiscordGithubBot.Tests.Ai;

public class ShortlistTests
{
    private static ShortlistSpec Spec(int chunkSize = 10, double floor = 0.05, int take = 3) =>
        new("test", "things", "Which thing in `things`?", "None of them.", chunkSize, floor, take);

    private static List<ShortlistItem> Items(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new ShortlistItem($"t{i}", $"Thing {i}", new JsonObject { ["name"] = $"thing {i}" }))
            .ToList();

    /// <summary>Answers every choice with the given weights; options not listed get nothing, "none" gets the rest.</summary>
    private static FakeDecisions Weights(params (string Key, double P)[] weights) => new((call, _, question) =>
    {
        var options = ((ChoiceQuestion)question).Options.Select(o => o.Key).ToList();
        var present = weights.Where(w => options.Contains(w.Key)).ToList();
        var rest = Math.Max(0, 1 - present.Sum(w => w.P));
        return FakeDecisions.Choice(present.Append((Shortlist.NoneKey, rest)).ToArray());
    });

    [Fact]
    public async Task No_items_means_no_call()
    {
        var decisions = Weights();

        Assert.Empty(await Shortlist.SelectAsync(decisions, new JsonObject(), [], Spec()));
        Assert.Empty(decisions.Calls);
    }

    [Fact]
    public async Task One_chunk_is_one_choice_with_every_item_and_none()
    {
        var decisions = Weights(("t2", 0.7), ("t3", 0.2));

        var picks = await Shortlist.SelectAsync(decisions, new JsonObject { ["report"] = "r" }, Items(4), Spec());

        Assert.Equal(["t2", "t3"], picks.Select(p => p.Key));
        var call = Assert.Single(decisions.Calls);
        Assert.Equal("r", call.State["report"]!.GetValue<string>());
        Assert.Equal("thing 3", call.State["things"]!["t3"]!["name"]!.GetValue<string>());
        var question = Assert.IsType<ChoiceQuestion>(call.Questions.Single().Value);
        Assert.Equal(["t1", "t2", "t3", "t4", Shortlist.NoneKey], question.Options.Select(o => o.Key));
    }

    [Fact]
    public async Task Items_below_the_floor_and_beyond_take_are_dropped()
    {
        var decisions = Weights(("t1", 0.4), ("t2", 0.3), ("t3", 0.2), ("t4", 0.06), ("t5", 0.04));

        var picks = await Shortlist.SelectAsync(decisions, new JsonObject(), Items(5), Spec(take: 3));

        Assert.Equal(["t1", "t2", "t3"], picks.Select(p => p.Key));
    }

    /// <summary>
    /// Each chunk's probabilities only compare within that chunk, so survivors from several chunks meet
    /// again in one final question — whose answer is what ranks them.
    /// </summary>
    [Fact]
    public async Task Survivors_of_several_chunks_are_asked_again_together()
    {
        var decisions = Weights(("t1", 0.5), ("t4", 0.3));

        var picks = await Shortlist.SelectAsync(decisions, new JsonObject(), Items(5), Spec(chunkSize: 2));

        Assert.Equal(4, decisions.Calls.Count); // three chunks, then the final
        var final = (ChoiceQuestion)decisions.Calls[^1].Questions.Single().Value;
        Assert.Equal(["t1", "t4", Shortlist.NoneKey], final.Options.Select(o => o.Key));
        Assert.Equal(["t1", "t4"], picks.Select(p => p.Key));
    }

    [Fact]
    public async Task A_single_survivor_across_chunks_needs_no_final_round()
    {
        var decisions = Weights(("t3", 0.9));

        var picks = await Shortlist.SelectAsync(decisions, new JsonObject(), Items(4), Spec(chunkSize: 2));

        Assert.Equal(2, decisions.Calls.Count);
        Assert.Equal("t3", Assert.Single(picks).Key);
    }

    [Fact]
    public async Task A_failed_call_throws()
    {
        await Assert.ThrowsAsync<OpenRouterException>(
            () => Shortlist.SelectAsync(FakeDecisions.Failing(), new JsonObject(), Items(2), Spec()));
    }
}
