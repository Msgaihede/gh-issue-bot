namespace DiscordGithubBot.CodeContext.Retrieval;

/// <summary>
/// Nearest neighbours by cosine similarity over embedding vectors, in memory. A repository map is at most a
/// couple of thousand vectors, so a linear scan is microseconds and needs no index structure. Vectors are
/// normalised once on the way in, which turns every comparison into a dot product.
/// </summary>
public sealed class VectorIndex
{
    private readonly List<(string Key, float[] Vector)> _items = new();

    public VectorIndex(IEnumerable<(string Key, float[] Vector)> items)
    {
        foreach (var (key, vector) in items) _items.Add((key, Normalize(vector)));
    }

    public int Count => _items.Count;

    /// <summary>The most similar items, most similar first. Score is the cosine similarity in [-1, 1].</summary>
    public IReadOnlyList<SearchHit> Search(float[] query, int take)
    {
        var q = Normalize(query);
        return _items
            .Where(i => i.Vector.Length == q.Length)
            .Select(i => new SearchHit(i.Key, Dot(i.Vector, q)))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Key, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += (double)a[i] * b[i];
        return sum;
    }

    private static float[] Normalize(float[] vector)
    {
        double norm = 0;
        foreach (var v in vector) norm += (double)v * v;
        norm = Math.Sqrt(norm);
        if (norm == 0) return (float[])vector.Clone();

        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = (float)(vector[i] / norm);
        return result;
    }
}

/// <summary>
/// Reciprocal rank fusion: merges ranked lists that score on different scales (BM25 and cosine similarity)
/// by rank alone. A document high in either list ranks high; one high in both ranks highest.
/// </summary>
public static class RankFusion
{
    /// <summary>The usual damping constant; it keeps rank 1 from dwarfing ranks 2–10.</summary>
    private const double K = 60;

    public static IReadOnlyList<SearchHit> Fuse(int take, params IReadOnlyList<SearchHit>[] rankings)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var ranking in rankings)
            for (var rank = 0; rank < ranking.Count; rank++)
                scores[ranking[rank].Key] = scores.GetValueOrDefault(ranking[rank].Key) + 1 / (K + rank + 1);

        return scores
            .Select(kv => new SearchHit(kv.Key, kv.Value))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Key, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }
}
