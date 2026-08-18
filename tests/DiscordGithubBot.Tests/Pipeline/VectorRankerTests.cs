using DiscordGithubBot.Data;
using DiscordGithubBot.Pipeline;

namespace DiscordGithubBot.Tests.Pipeline;

public class VectorRankerTests
{
    private static IssueEmbedding Issue(int number, params float[] v) => new()
    {
        RepoKey = "o/r", IssueNumber = number, Title = $"#{number}", State = "open",
        ContentHash = "h", Vector = v,
    };

    [Fact]
    public void Ranks_by_cosine_similarity_descending()
    {
        float[] query = [1f, 0f, 0f];
        var ranked = VectorRanker.TopK(query,
            [Issue(1, 0f, 1f, 0f), Issue(2, 1f, 0f, 0f), Issue(3, 0.9f, 0.1f, 0f)], 5);
        Assert.Equal([2, 3, 1], ranked.Select(r => r.Issue.IssueNumber).ToArray());
        Assert.Equal(1f, ranked[0].Score, 3);
    }

    [Fact]
    public void Returns_at_most_k()
    {
        float[] query = [1f, 0f];
        var ranked = VectorRanker.TopK(query,
            Enumerable.Range(1, 10).Select(i => Issue(i, 1f, i / 10f)), 5);
        Assert.Equal(5, ranked.Count);
    }

    [Fact]
    public void Skips_dimension_mismatches_and_empty_vectors()
    {
        float[] query = [1f, 0f];
        var ranked = VectorRanker.TopK(query,
            [Issue(1, 1f, 0f, 0f), Issue(2), Issue(3, 1f, 0f)], 5);
        Assert.Equal(3, Assert.Single(ranked).Issue.IssueNumber);
    }

    [Fact]
    public void Empty_candidates_gives_empty_result() =>
        Assert.Empty(VectorRanker.TopK(new float[] { 1f }, [], 5));
}
