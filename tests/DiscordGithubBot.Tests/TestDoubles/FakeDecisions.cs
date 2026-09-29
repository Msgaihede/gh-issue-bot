using System.Text.Json.Nodes;
using DiscordGithubBot.OpenRouter;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>
/// Scripted <see cref="IDecisionModel"/>: a function answers each question of each call, so a test can
/// answer by question key, by option, or by what the state contains. Records every call.
/// </summary>
public sealed class FakeDecisions : IDecisionModel
{
    public sealed record Call(string Purpose, JsonNode State, IReadOnlyDictionary<string, DecisionQuestion> Questions);

    private readonly Func<Call, string, DecisionQuestion, DecisionAnswer> _answer;

    /// <param name="answer">(call, question key, question) → answer; throw from it to fail the call.</param>
    public FakeDecisions(Func<Call, string, DecisionQuestion, DecisionAnswer> answer) => _answer = answer;

    public List<Call> Calls { get; } = new();

    public Task<DecisionResult> DecideAsync(
        string purpose, JsonNode state, IReadOnlyDictionary<string, DecisionQuestion> questions,
        CancellationToken ct = default)
    {
        var call = new Call(purpose, state.DeepClone(), questions);
        lock (Calls) Calls.Add(call);

        try
        {
            var answers = questions.ToDictionary(q => q.Key, q => _answer(call, q.Key, q.Value));
            return Task.FromResult(new DecisionResult("fake/jev", answers));
        }
        catch (Exception ex)
        {
            return Task.FromException<DecisionResult>(ex);
        }
    }

    /// <summary>Every call fails as the real client does when OpenRouter is unreachable.</summary>
    public static FakeDecisions Failing() =>
        new((_, _, _) => throw new OpenRouterException("unreachable", null, isTransient: true));

    /// <summary>A choice answer with the given probabilities; the most probable option is the choice.</summary>
    public static ChoiceAnswer Choice(params (string Option, double P)[] probabilities)
    {
        var map = probabilities.ToDictionary(p => p.Option, p => p.P);
        return new ChoiceAnswer(probabilities.MaxBy(p => p.P).Option, map, null);
    }

    /// <summary>A choice answer that puts all its weight on one option.</summary>
    public static ChoiceAnswer Chosen(string option) => Choice((option, 1.0));

    public static NoulAnswer Noul(double p) => new(p);

    /// <summary>A score answer from per-level probabilities, lowest level first.</summary>
    public static ScoreAnswer Score(params double[] levelProbabilities)
    {
        var map = levelProbabilities.Select((p, i) => (i, p)).ToDictionary(x => x.i, x => x.p);
        return new ScoreAnswer(map.Sum(kv => kv.Key * kv.Value), map, null);
    }
}
