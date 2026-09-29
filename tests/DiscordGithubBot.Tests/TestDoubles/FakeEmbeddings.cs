using DiscordGithubBot.OpenRouter;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>
/// Scripted <see cref="IEmbeddingModel"/>: a function turns each input into a vector (by default every input maps
/// to the same vector, so every file is equally similar). Records every call; <see cref="Fail"/> makes calls throw.
/// </summary>
public sealed class FakeEmbeddings(Func<string, float[]>? vectorFor = null) : IEmbeddingModel
{
    public List<(string Model, IReadOnlyList<string> Inputs)> Calls { get; } = new();

    public bool Fail { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        lock (Calls) Calls.Add((model, inputs));
        if (Fail) return Task.FromException<IReadOnlyList<float[]>>(new OpenRouterException("down", null, isTransient: true));

        var embed = vectorFor ?? (_ => [1f, 0f]);
        return Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(embed).ToList());
    }
}
