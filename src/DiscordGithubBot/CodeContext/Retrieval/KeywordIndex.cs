namespace DiscordGithubBot.CodeContext.Retrieval;

/// <summary>A document to index: its key (e.g. a path) and the text it is found by.</summary>
public sealed record IndexedText(string Key, string Text);

/// <summary>A search hit: the document's key and how well it matched (higher is better; only comparable within one search).</summary>
public sealed record SearchHit(string Key, double Score);

/// <summary>
/// Okapi BM25 over a small in-memory corpus — here, a repository map's paths and summaries. Built per use:
/// a couple of thousand one-line documents index in milliseconds, so there is nothing to persist or keep in
/// sync. BM25 rewards a query term more the rarer it is across the corpus, so the words that single out a
/// feature ("apostrophe", "fts") outweigh the ones every file shares ("src", "component").
/// </summary>
public sealed class KeywordIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    private readonly List<(string Key, Dictionary<string, int> Terms, int Length)> _documents = new();
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    public KeywordIndex(IEnumerable<IndexedText> documents)
    {
        foreach (var document in documents)
        {
            var terms = new Dictionary<string, int>(StringComparer.Ordinal);
            var length = 0;
            foreach (var token in TextTokens.Of(document.Text))
            {
                terms[token] = terms.GetValueOrDefault(token) + 1;
                length++;
            }

            foreach (var term in terms.Keys) _documentFrequency[term] = _documentFrequency.GetValueOrDefault(term) + 1;
            _documents.Add((document.Key, terms, length));
        }

        _averageLength = _documents.Count == 0 ? 1 : Math.Max(1, _documents.Average(d => d.Length));
    }

    public int Count => _documents.Count;

    /// <summary>
    /// The best-matching documents, best first; documents sharing no term with the query are left out. Each
    /// distinct query term counts once, so a term repeated by query expansion cannot drown out the rest.
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, int take)
    {
        var terms = TextTokens.Of(query).Distinct(StringComparer.Ordinal)
            .Where(_documentFrequency.ContainsKey)
            .ToList();
        if (terms.Count == 0) return [];

        var n = _documents.Count;
        var idf = terms.ToDictionary(t => t, t =>
        {
            var df = _documentFrequency[t];
            return Math.Log(1 + (n - df + 0.5) / (df + 0.5));
        });

        return _documents
            .Select(d =>
            {
                double score = 0;
                foreach (var term in terms)
                {
                    if (!d.Terms.TryGetValue(term, out var tf)) continue;
                    score += idf[term] * tf * (K1 + 1) / (tf + K1 * (1 - B + B * d.Length / _averageLength));
                }
                return new SearchHit(d.Key, score);
            })
            .Where(h => h.Score > 0)
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Key, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }
}
