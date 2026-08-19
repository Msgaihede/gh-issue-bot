using System.Numerics.Tensors;
using DiscordGithubBot.Data;

namespace DiscordGithubBot.Pipeline;

/// <summary>An issue scored against a query embedding; a higher score means more similar.</summary>
public sealed record RankedIssue(IssueEmbedding Issue, float Score);

/// <summary>Ranks cached issue embeddings against a query embedding by cosine similarity.</summary>
public static class VectorRanker
{
    /// <summary>Dimension of every embedding vector; the single source of truth.</summary>
    public const int EmbeddingDimensions = 1536;

    /// <summary>
    /// Returns at most <paramref name="k"/> candidates ordered by descending cosine similarity to
    /// <paramref name="query"/>. Candidates with an empty vector or a differing dimension are skipped.
    /// </summary>
    public static IReadOnlyList<RankedIssue> TopK(
        ReadOnlyMemory<float> query, IEnumerable<IssueEmbedding> candidates, int k)
    {
        var results = new List<RankedIssue>();
        foreach (var c in candidates)
        {
            if (c.Vector.Length == 0 || c.Vector.Length != query.Length) continue;
            var score = TensorPrimitives.CosineSimilarity(query.Span, c.Vector);
            results.Add(new RankedIssue(c, score));
        }
        return results.OrderByDescending(r => r.Score).Take(k).ToList();
    }
}
