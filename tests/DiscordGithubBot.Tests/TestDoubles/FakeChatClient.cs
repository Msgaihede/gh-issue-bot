using Microsoft.Extensions.AI;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>Returns scripted assistant texts in sequence; records prompts. Works with GetResponseAsync&lt;T&gt; because the structured-output layer parses assistant text as JSON.</summary>
public sealed class FakeChatClient(params string[] responses) : IChatClient
{
    private int _call;
    public List<string> Prompts { get; } = new();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));
        var text = responses[Math.Min(_call++, responses.Length - 1)];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
