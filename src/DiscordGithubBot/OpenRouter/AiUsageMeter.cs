using System.Globalization;

namespace DiscordGithubBot.OpenRouter;

/// <summary>
/// Adds up what the OpenRouter calls made inside one DI scope cost — one report, one confirmation click,
/// one repo-map refresh — so the budget this bot is designed against can be read off the logs instead of
/// estimated. Scoped, and locked because a report fans its decision calls out in parallel.
/// </summary>
/// <remarks>
/// With BYOK (the account's own provider keys behind OpenRouter) the dollars are only partly visible here.
/// Chat responses report the provider's charge as an upstream cost, which the chat client counts. Decision
/// responses report no upstream cost at all, so the meter also counts the decision model's input tokens —
/// the quantity the decision provider bills on — rather than guessing a price in code.
/// </remarks>
public sealed class AiUsageMeter
{
    private readonly Lock _gate = new();
    private decimal _cost;
    private int _calls;
    private long _decisionInputTokens;

    /// <param name="cost">USD the response reported (for chat, including a BYOK upstream charge)</param>
    /// <param name="decisionInputTokens">input tokens of a decision call; 0 for chat calls</param>
    public void Record(decimal? cost, int decisionInputTokens = 0)
    {
        lock (_gate)
        {
            _calls++;
            _cost += cost ?? 0m;
            _decisionInputTokens += decisionInputTokens;
        }
    }

    /// <summary>USD spent so far in this scope, as the responses reported it.</summary>
    public decimal TotalCost
    {
        get { lock (_gate) return _cost; }
    }

    public int Calls
    {
        get { lock (_gate) return _calls; }
    }

    /// <summary>Input tokens read by the decision model — its billing unit, and the whole picture under BYOK.</summary>
    public long DecisionInputTokens
    {
        get { lock (_gate) return _decisionInputTokens; }
    }

    /// <summary>One line for logs: "$0.00041 in 5 call(s), 12,345 decision-model input tokens".</summary>
    public override string ToString()
    {
        lock (_gate)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"${_cost:0.#####} in {_calls} call(s), {_decisionInputTokens:N0} decision-model input tokens");
        }
    }
}
