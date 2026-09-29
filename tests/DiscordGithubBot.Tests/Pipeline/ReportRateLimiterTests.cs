using DiscordGithubBot.Configuration;
using DiscordGithubBot.Pipeline;

namespace DiscordGithubBot.Tests.Pipeline;

public class ReportRateLimiterTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualClock _clock = new();

    private ReportRateLimiter Sut(int perDay) =>
        new(new BotOptions { Limits = { ReportsPerUserPerDay = perDay } }, _clock);

    [Fact]
    public void A_user_under_the_limit_may_report()
    {
        var sut = Sut(2);
        sut.Record(1);

        Assert.Null(sut.RetryAfter(1));
    }

    [Fact]
    public void A_user_at_the_limit_is_told_when_the_oldest_report_leaves_the_window()
    {
        var sut = Sut(2);
        var first = _clock.Now;
        sut.Record(1);
        _clock.Now += TimeSpan.FromHours(3);
        sut.Record(1);

        Assert.Equal(first + ReportRateLimiter.Window, sut.RetryAfter(1));
    }

    [Fact]
    public void The_window_rolls()
    {
        var sut = Sut(1);
        sut.Record(1);

        _clock.Now += ReportRateLimiter.Window;

        Assert.Null(sut.RetryAfter(1));
    }

    [Fact]
    public void Users_are_counted_separately()
    {
        var sut = Sut(1);
        sut.Record(1);

        Assert.NotNull(sut.RetryAfter(1));
        Assert.Null(sut.RetryAfter(2));
    }

    [Fact]
    public void Zero_turns_the_cap_off()
    {
        var sut = Sut(0);
        for (var i = 0; i < 100; i++) sut.Record(1);

        Assert.Null(sut.RetryAfter(1));
    }
}
