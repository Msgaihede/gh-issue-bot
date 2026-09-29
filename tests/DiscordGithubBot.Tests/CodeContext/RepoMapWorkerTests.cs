using DiscordGithubBot.CodeContext;

namespace DiscordGithubBot.Tests.CodeContext;

public sealed class RepoMapWorkerTests
{
    /// <summary>A first build can outlast the interval; the tick that finds it running must not start a second one.</summary>
    [Fact]
    public async Task A_check_is_not_started_while_the_previous_one_runs()
    {
        var runner = new NonOverlappingRunner();
        var first = new TaskCompletionSource();
        var starts = 0;

        Assert.True(runner.TryStart(() => { starts++; return first.Task; }));
        Assert.False(runner.TryStart(() => { starts++; return Task.CompletedTask; }));

        first.SetResult();
        await runner.Current;

        Assert.True(runner.TryStart(() => { starts++; return Task.CompletedTask; }));
        await runner.Current;
        Assert.Equal(2, starts);
    }

    /// <summary>Work that blocks synchronously would hang the caller if it ran inline; on the pool it cannot.</summary>
    [Fact]
    public async Task A_check_runs_on_the_thread_pool_not_on_the_callers_path()
    {
        var runner = new NonOverlappingRunner();
        using var gate = new ManualResetEventSlim();

        Assert.True(runner.TryStart(() => { gate.Wait(); return Task.CompletedTask; }));
        Assert.False(runner.Current.IsCompleted);

        gate.Set();
        await runner.Current;
    }

    [Fact]
    public async Task A_failed_check_does_not_block_the_next()
    {
        var runner = new NonOverlappingRunner();
        runner.TryStart(() => throw new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.Current);

        Assert.True(runner.TryStart(() => Task.CompletedTask));
    }
}
