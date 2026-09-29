using System.Text.Json.Nodes;
using DiscordGithubBot.OpenRouter;

namespace DiscordGithubBot.Ai;

/// <summary>One candidate a shortlist may pick: its option key, what choosing it means, and its entry in state.</summary>
/// <param name="Key">Option name and state key, e.g. <c>issue_42</c>; must not be <see cref="Shortlist.NoneKey"/>.</param>
/// <param name="Criterion">The option's criterion; names the candidate so it stands without its index.</param>
public sealed record ShortlistItem(string Key, string Criterion, JsonNode State);

public sealed record ShortlistPick(string Key, double Probability);

/// <param name="Purpose">Names the decision calls in logs.</param>
/// <param name="ItemsField">State field the candidates are placed under, e.g. <c>issues</c>.</param>
/// <param name="Instructions">The choice question; should reference <paramref name="ItemsField"/> by name.</param>
/// <param name="NoneCriterion">What the no-match option means.</param>
/// <param name="ChunkSize">Most candidates per request — the decision model's context bounds this.</param>
/// <param name="Floor">Least probability a candidate needs to survive a round.</param>
/// <param name="Take">Most candidates returned.</param>
public sealed record ShortlistSpec(
    string Purpose, string ItemsField, string Instructions, string NoneCriterion,
    int ChunkSize, double Floor, int Take);

/// <summary>
/// Narrows many candidates to the few worth a closer look, with the decision model reading every one of
/// them — TypeSafe's "rank, then re-check" pattern. Each round is a single <c>choice</c> whose options are
/// the candidates plus <c>none</c>, and the answer's probability distribution is the ranking. The
/// decision model's context bounds how many candidates fit one request, so large sets are split into
/// chunks asked in parallel; each chunk's distribution is normalised over its own options only, so when
/// several chunks produce survivors, a second round over just the survivors puts them on one scale.
/// </summary>
public static class Shortlist
{
    public const string NoneKey = "none";

    private const string QuestionKey = "pick";

    /// <returns>The surviving candidates, most probable first; empty when nothing clears the floor.</returns>
    /// <exception cref="OpenRouterException">a decision call failed</exception>
    public static async Task<IReadOnlyList<ShortlistPick>> SelectAsync(
        IDecisionModel decisions, JsonObject sharedState, IReadOnlyList<ShortlistItem> items, ShortlistSpec spec,
        CancellationToken ct = default)
    {
        if (items.Count == 0) return [];

        var chunks = items.Chunk(spec.ChunkSize).ToList();
        var rounds = await Task.WhenAll(chunks.Select(chunk => AskAsync(decisions, sharedState, chunk, spec, ct)));
        var survivors = rounds.SelectMany(r => r).Where(p => p.Probability >= spec.Floor).ToList();

        if (chunks.Count > 1 && survivors.Count > 1)
        {
            var byKey = items.ToDictionary(i => i.Key);
            var finalists = survivors
                .OrderByDescending(p => p.Probability)
                .Take(spec.ChunkSize)
                .Select(p => byKey[p.Key])
                .ToList();

            survivors = (await AskAsync(decisions, sharedState, finalists, spec, ct))
                .Where(p => p.Probability >= spec.Floor)
                .ToList();
        }

        return survivors.OrderByDescending(p => p.Probability).Take(spec.Take).ToList();
    }

    private static async Task<IReadOnlyList<ShortlistPick>> AskAsync(
        IDecisionModel decisions, JsonObject sharedState, IReadOnlyList<ShortlistItem> items, ShortlistSpec spec,
        CancellationToken ct)
    {
        var state = (JsonObject)sharedState.DeepClone();
        var entries = new JsonObject();
        foreach (var item in items) entries[item.Key] = item.State.DeepClone();
        state[spec.ItemsField] = entries;

        var options = items
            .Select(i => new ChoiceOption(i.Key, i.Criterion))
            .Append(new ChoiceOption(NoneKey, spec.NoneCriterion))
            .ToList();

        var result = await decisions.DecideAsync(
            spec.Purpose, state,
            new Dictionary<string, DecisionQuestion> { [QuestionKey] = new ChoiceQuestion(spec.Instructions, options) },
            ct);
        var answer = result.Get<ChoiceAnswer>(QuestionKey);

        return items.Select(i => new ShortlistPick(i.Key, answer.Probability(i.Key))).ToList();
    }
}
