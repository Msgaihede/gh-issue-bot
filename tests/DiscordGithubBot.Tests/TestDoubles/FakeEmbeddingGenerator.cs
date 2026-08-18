using Microsoft.Extensions.AI;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>Deterministic embedder: vector = f(text hash); records inputs.</summary>
public sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public List<string> Inputs { get; } = new();

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = new List<Embedding<float>>();
        foreach (var v in values)
        {
            Inputs.Add(v);
            var seed = (float)(Math.Abs(v.GetHashCode()) % 1000) / 1000f;
            list.Add(new Embedding<float>(new float[] { seed, 1f - seed, 0.5f }));
        }
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
