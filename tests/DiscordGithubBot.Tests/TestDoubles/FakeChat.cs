using DiscordGithubBot.OpenRouter;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>
/// Scripted <see cref="IOpenRouterChat"/>: each call takes the next scripted reply (the last one repeats) —
/// either an answer object, which must be of the requested type, or an exception to throw. Records prompts.
/// </summary>
public sealed class FakeChat(params object[] replies) : IOpenRouterChat
{
    private int _call;

    public List<(ChatPrompt Prompt, ChatUrgency Urgency)> Calls { get; } = new();

    public Task<T> CompleteAsync<T>(ChatPrompt prompt, ChatUrgency urgency, CancellationToken ct = default)
        where T : class
    {
        Calls.Add((prompt, urgency));
        var reply = replies[Math.Min(_call++, replies.Length - 1)];

        return reply switch
        {
            Exception ex => Task.FromException<T>(ex),
            T answer => Task.FromResult(answer),
            // Answers are given as JSON when the DTO is private to the class under test.
            string json => Task.FromResult(StructuredOutput.Parse<T>(json)!),
            _ => throw new InvalidOperationException($"Scripted reply {reply.GetType().Name} is not a {typeof(T).Name}."),
        };
    }
}
