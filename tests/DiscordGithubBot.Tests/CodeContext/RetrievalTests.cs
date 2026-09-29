using System.Net;
using System.Text.Json;
using DiscordGithubBot.Ai;
using DiscordGithubBot.CodeContext.Retrieval;
using DiscordGithubBot.OpenRouter;
using DiscordGithubBot.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordGithubBot.Tests.CodeContext;

public class TextTokensTests
{
    /// <summary>Code and prose name things differently; both must land on the same terms.</summary>
    [Theory]
    [InlineData("fetchCardImages", new[] { "fetch", "card", "image" })]
    [InlineData("card_images.rs", new[] { "card", "image", "rs" })]
    [InlineData("HTTPServer", new[] { "http", "server" })]
    [InlineData("fts5Query", new[] { "fts5", "query" })]
    [InlineData("The card pictures are not showing", new[] { "card", "picture", "show" })]
    public void Identifiers_and_prose_split_into_the_same_kind_of_terms(string text, string[] expected) =>
        Assert.Equal(expected, TextTokens.Of(text));

    [Theory]
    [InlineData("searching", "search")]
    [InlineData("searches", "search")]
    [InlineData("libraries", "library")]
    [InlineData("stopped", "stop")]
    [InlineData("class", "class")]
    [InlineData("status", "status")]
    public void The_stemmer_folds_common_forms(string word, string stem) => Assert.Equal(stem, TextTokens.Stem(word));
}

public class KeywordIndexTests
{
    private static KeywordIndex Index() => new(
    [
        new("src/search/query.ts", "Builds full-text search queries and escapes quotes and apostrophes."),
        new("src/components/CardImage.tsx", "Renders a card's image with lazy loading and a placeholder."),
        new("src/components/SearchBar.tsx", "Search input component with suggestions."),
        new("docs/search-syntax.md", "Documents the search syntax, quoting rules and operators."),
    ]);

    [Fact]
    public void The_best_match_comes_first_and_non_matches_are_left_out()
    {
        var hits = Index().Search("apostrophe in search query", 10);

        Assert.Equal("src/search/query.ts", hits[0].Key);
        Assert.DoesNotContain(hits, h => h.Key == "src/components/CardImage.tsx");
    }

    /// <summary>The path is part of what a file is found by — "CardImage" must answer "card image".</summary>
    [Fact]
    public void Paths_are_searchable_through_their_identifier_parts()
    {
        Assert.Equal("src/components/CardImage.tsx", Index().Search("card image", 1).Single().Key);
    }

    [Fact]
    public void A_query_sharing_nothing_with_the_corpus_finds_nothing() =>
        Assert.Empty(Index().Search("billing invoice", 10));

    /// <summary>Expansion can repeat a word; each distinct term still counts once.</summary>
    [Fact]
    public void Repeating_a_term_does_not_change_the_ranking()
    {
        var once = Index().Search("image search", 4).Select(h => h.Key);
        var repeated = Index().Search("image image image image search", 4).Select(h => h.Key);

        Assert.Equal(once, repeated);
    }
}

public class VectorIndexTests
{
    [Fact]
    public void Nearest_vectors_come_first_regardless_of_their_length()
    {
        var index = new VectorIndex([("a", [1f, 0f]), ("b", [0f, 5f]), ("c", [3f, 3f])]);

        Assert.Equal(["b", "c", "a"], index.Search([0f, 1f], 3).Select(h => h.Key));
        Assert.Equal(1.0, index.Search([0f, 2f], 1).Single().Score, 5);
    }

    [Fact]
    public void Fusion_ranks_what_both_lists_agree_on_highest()
    {
        IReadOnlyList<SearchHit> keyword = [new("x", 9), new("both", 5), new("y", 1)];
        IReadOnlyList<SearchHit> vector = [new("z", 0.9), new("both", 0.8)];

        var fused = RankFusion.Fuse(10, keyword, vector);

        Assert.Equal("both", fused[0].Key);
        Assert.Equal(4, fused.Count);
    }
}

public class EmbeddingClientTests
{
    [Fact]
    public async Task Vectors_come_back_in_input_order_and_the_cost_is_recorded()
    {
        var http = new ScriptedHttpHandler().Then(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { index = 1, embedding = new[] { 0f, 1f } },
                new { index = 0, embedding = new[] { 1f, 0f } },
            },
            usage = new { cost = 0.000002m },
        }));
        var usage = new AiUsageMeter();

        var vectors = await new EmbeddingClient(http.CreateClient(), usage, NullLogger<EmbeddingClient>.Instance)
            .EmbedAsync("voyageai/voyage-4-lite", ["first", "second"]);

        Assert.Equal([1f, 0f], vectors[0]);
        Assert.Equal([0f, 1f], vectors[1]);
        Assert.Equal(0.000002m, usage.TotalCost);
        var body = http.Requests.Single().Body!;
        Assert.Equal("voyageai/voyage-4-lite", body["model"]!.GetValue<string>());
        Assert.EndsWith("/api/v1/embeddings", http.Requests.Single().Url);
    }

    [Fact]
    public async Task Large_inputs_go_in_batches()
    {
        var http = new ScriptedHttpHandler();
        var inputs = Enumerable.Range(0, EmbeddingClient.BatchSize + 1).Select(i => $"t{i}").ToList();
        http.Then(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = Enumerable.Range(0, EmbeddingClient.BatchSize).Select(i => new { index = i, embedding = new[] { 1f } }),
        })).Then(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new[] { new { index = 0, embedding = new[] { 1f } } } }));

        var vectors = await new EmbeddingClient(http.CreateClient(), new AiUsageMeter(), NullLogger<EmbeddingClient>.Instance)
            .EmbedAsync("m", inputs);

        Assert.Equal(inputs.Count, vectors.Count);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task A_response_missing_vectors_is_an_error()
    {
        var http = new ScriptedHttpHandler().Then(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1]}]}""");

        await Assert.ThrowsAsync<OpenRouterException>(() =>
            new EmbeddingClient(http.CreateClient(), new AiUsageMeter(), NullLogger<EmbeddingClient>.Instance)
                .EmbedAsync("m", ["a", "b"]));
    }
}

public class QueryExpanderTests
{
    private static readonly IssueDraft Draft = new("Card pictures never show up", "The pictures on the cards stay blank.");

    [Fact]
    public async Task Terms_are_cleaned_and_deduplicated()
    {
        var chat = new FakeChat("""{"terms":["image cache"," thumbnail ","Image Cache","","render"]}""");

        var terms = await new QueryExpander(chat, NullLogger<QueryExpander>.Instance).ExpandAsync(Draft, null);

        Assert.Equal(["image cache", "thumbnail", "render"], terms);
    }

    [Fact]
    public async Task The_file_tree_is_given_to_the_model_only_when_asked()
    {
        var chat = new FakeChat("""{"terms":["x"]}""");
        var sut = new QueryExpander(chat, NullLogger<QueryExpander>.Instance);

        await sut.ExpandAsync(Draft, null);
        await sut.ExpandAsync(Draft, QueryExpander.FileTree(["src/lib/image_cache.rs"]));

        Assert.DoesNotContain("file tree:", chat.Calls[0].Prompt.User);
        Assert.Contains("image_cache.rs", chat.Calls[1].Prompt.User);
    }

    [Fact]
    public async Task A_failed_call_means_no_terms()
    {
        var chat = new FakeChat(new OpenRouterException("down", null, isTransient: true));

        Assert.Empty(await new QueryExpander(chat, NullLogger<QueryExpander>.Instance).ExpandAsync(Draft, null));
    }

    [Fact]
    public void The_file_tree_lists_each_directory_once()
    {
        var tree = QueryExpander.FileTree(["src/b.ts", "src/a.ts", "README.md", "src/lib/c.rs"]);

        Assert.Equal("./\n  README.md\nsrc/\n  a.ts\n  b.ts\nsrc/lib/\n  c.rs", tree);
    }
}
