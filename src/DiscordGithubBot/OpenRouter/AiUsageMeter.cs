namespace DiscordGithubBot.OpenRouter;

/// <summary>
/// Adds up what the OpenRouter calls made inside one DI scope cost — one report, one confirmation click,
/// one repo-map refresh. OpenRouter prices every response (<c>usage.cost</c>, in USD), so the budget this
/// bot is designed against can be read straight off the logs instead of estimated. Scoped, and locked
/// because a report fans its decision calls out in parallel.
/// </summary>
public sealed class AiUsageMeter
{
    private readonly Lock _gate = new();
    private decimal _cost;
    private int _calls;

    public void Record(decimal? cost)
    {
        lock (_gate)
        {
            _calls++;
            _cost += cost ?? 0m;
        }
    }

    /// <summary>USD spent so far in this scope.</summary>
    public decimal TotalCost
    {
        get { lock (_gate) return _cost; }
    }

    public int Calls
    {
        get { lock (_gate) return _calls; }
    }
}
