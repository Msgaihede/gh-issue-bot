using DiscordGithubBot.Configuration;

namespace DiscordGithubBot.Pipeline;

/// <summary>
/// Caps how many reports one Discord user can submit in a rolling day. With the bot user-installable,
/// anyone on Discord can reach every configured repository, and every submitted report spends money on
/// model calls — so without a cap, one account could burn the month's budget in an afternoon. The window
/// is in memory: a restart forgives everyone, which is acceptable for a guard against runaway use (the
/// hard cap is the credit limit on the OpenRouter key).
/// </summary>
public sealed class ReportRateLimiter(BotOptions options, TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    private readonly Dictionary<ulong, Queue<DateTimeOffset>> _submissions = new();
    private readonly Lock _gate = new();

    private int Limit => options.Limits.ReportsPerUserPerDay;

    /// <returns>null when the user may submit another report; otherwise when they next may.</returns>
    public DateTimeOffset? RetryAfter(ulong userId)
    {
        if (Limit <= 0) return null;

        lock (_gate)
        {
            if (!_submissions.TryGetValue(userId, out var queue)) return null;

            Prune(queue);
            return queue.Count < Limit ? null : queue.Peek() + Window;
        }
    }

    /// <summary>Counts a submitted report against the user's window.</summary>
    public void Record(ulong userId)
    {
        if (Limit <= 0) return;

        lock (_gate)
        {
            if (!_submissions.TryGetValue(userId, out var queue)) _submissions[userId] = queue = new Queue<DateTimeOffset>();

            Prune(queue);
            queue.Enqueue(time.GetUtcNow());
        }
    }

    private void Prune(Queue<DateTimeOffset> queue)
    {
        var cutoff = time.GetUtcNow() - Window;
        while (queue.Count > 0 && queue.Peek() <= cutoff) queue.Dequeue();
    }
}
