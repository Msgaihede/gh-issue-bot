using Microsoft.Extensions.AI;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>
/// Fails the first call the way <see cref="HttpClient"/> and the OpenAI client report their own
/// timeouts — a <see cref="TaskCanceledException"/> with nobody's token cancelled — and then answers
/// with the scripted texts like <see cref="FakeChatClient"/>.
/// </summary>
public sealed class TimingOutChatClient(params string[] responsesAfterTimeout) : IChatClient
{
    private int _call;
    public List<string> Prompts { get; } = new();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Prompts.Add(string.Join("\n", messages.Select(m => m.Text)));

        if (_call++ == 0) throw new TaskCanceledException("timeout", new TimeoutException());

        var text = responsesAfterTimeout[Math.Min(_call - 1, responsesAfterTimeout.Length) - 1];
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
