using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Tests.TestDoubles;

/// <summary>Records every enabled log entry as its level and formatted message.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IEnumerable<string> Messages(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
    }
}
