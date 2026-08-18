using System.Text;
using DiscordGithubBot.Data;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Ai;

public enum VerdictKind { Match, Uncertain, NoMatch }

/// <param name="IssueNumber">set when Kind == Match</param>
/// <param name="CandidateNumbers">issue numbers worth showing when Kind == Uncertain (subset of input candidates)</param>
public sealed record DuplicateVerdict(VerdictKind Kind, int? IssueNumber, IReadOnlyList<int> CandidateNumbers);

public interface IDuplicateJudge
{
    /// <summary>Empty candidates short-circuits to NoMatch without an LLM call. LLM/parse failure degrades to Uncertain over all candidates.</summary>
    Task<DuplicateVerdict> JudgeAsync(IssueDraft draft, IReadOnlyList<IssueEmbedding> candidates, CancellationToken ct = default);
}

/// <summary>
/// Asks the model whether a new report duplicates one of the vector-ranked candidates. Every failure mode —
/// an unparseable answer, a thrown call, a match on an issue number that was never offered — degrades to
/// <see cref="VerdictKind.Uncertain"/> over all candidates, which asks the reporter instead of guessing.
/// </summary>
public sealed class DuplicateJudge(IChatClient chat, ILogger<DuplicateJudge> logger) : IDuplicateJudge
{
    /// <summary>Longest candidate body handed to the model; keeps a full candidate list within the context budget.</summary>
    private const int MaxExcerptChars = 1000;

    /// <summary>Shape requested from the model; property names map to the JSON schema sent with the request.</summary>
    private sealed class VerdictDto
    {
        public string Verdict { get; set; } = "no_match";
        public int? IssueNumber { get; set; }
        public int[]? Candidates { get; set; }
    }

    public async Task<DuplicateVerdict> JudgeAsync(
        IssueDraft draft, IReadOnlyList<IssueEmbedding> candidates, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return new DuplicateVerdict(VerdictKind.NoMatch, null, []);

        var numbers = candidates.Select(c => c.IssueNumber).ToList();

        try
        {
            var response = await chat.GetResponseAsync<VerdictDto>(
                BuildPrompt(draft, candidates), cancellationToken: ct);

            // TryGetResult, never .Result: an unparseable answer is a degradation, not an exception.
            if (response.TryGetResult(out var dto)) return Map(dto, numbers);

            logger.LogWarning(
                "The duplicate judge returned unparseable output; treating all {Count} candidates as uncertain.",
                numbers.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "The duplicate judge failed; treating all {Count} candidates as uncertain.", numbers.Count);
        }

        return Uncertain(numbers);
    }

    private static DuplicateVerdict Map(VerdictDto dto, IReadOnlyList<int> numbers) =>
        dto.Verdict?.Trim().ToLowerInvariant() switch
        {
            // A match on an issue we never offered is a hallucinated number: ask the reporter instead.
            "match" when dto.IssueNumber is { } number && numbers.Contains(number)
                => new DuplicateVerdict(VerdictKind.Match, number, []),
            "match" => Uncertain(numbers),
            "uncertain" => Uncertain(Intersect(dto.Candidates, numbers) is { Count: > 0 } shortlist
                ? shortlist
                : numbers),
            "no_match" => new DuplicateVerdict(VerdictKind.NoMatch, null, []),
            _ => Uncertain(numbers),
        };

    private static DuplicateVerdict Uncertain(IReadOnlyList<int> numbers) =>
        new(VerdictKind.Uncertain, null, numbers);

    /// <summary>Keeps only offered issue numbers, in the candidates' ranked order.</summary>
    private static List<int> Intersect(int[]? chosen, IReadOnlyList<int> numbers) =>
        chosen is null ? [] : numbers.Where(chosen.Contains).ToList();

    private static string BuildPrompt(IssueDraft draft, IReadOnlyList<IssueEmbedding> candidates)
    {
        var existing = new StringBuilder();
        foreach (var c in candidates)
        {
            existing.Append('#').Append(c.IssueNumber)
                .Append(" [").Append(c.State).Append("] ").AppendLine(c.Title)
                .AppendLine(Truncate(c.BodyExcerpt, MaxExcerptChars))
                .AppendLine();
        }

        return $"""
            Decide whether a new report describes the same underlying issue as one of the existing GitHub
            issues below.

            New report title: {draft.Title}
            New report body:
            {draft.Body}

            Existing issues:
            {existing.ToString().TrimEnd()}

            Answer with exactly one verdict:
            - "match" - the new report describes the same underlying issue as exactly one existing issue.
              Also give that issue's number.
            - "uncertain" - it could be one of several existing issues. Also list those issue numbers.
            - "no_match" - none of the existing issues describes the same underlying issue.

            Only answer match when you are confident it is the same defect/request, not merely the same feature area.
            Use only the issue numbers listed above.
            """;
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];
}
