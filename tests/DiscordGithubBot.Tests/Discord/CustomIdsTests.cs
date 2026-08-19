using DiscordGithubBot.Discord;

namespace DiscordGithubBot.Tests.Discord;

public class CustomIdsTests
{
    [Fact]
    public void Round_trips_action_guid_and_issue_number()
    {
        var id = Guid.NewGuid();
        var s = CustomIds.Build(CustomIds.Comment, id, 42);
        Assert.True(CustomIds.TryParse(s, out var action, out var parsedId, out var n));
        Assert.Equal("comment", action);
        Assert.Equal(id, parsedId);
        Assert.Equal(42, n);
    }

    [Fact]
    public void Default_issue_number_is_zero()
    {
        Assert.True(CustomIds.TryParse(CustomIds.Build(CustomIds.Cancel, Guid.NewGuid()), out _, out _, out var n));
        Assert.Equal(0, n);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rep|create")]                 // too few segments
    [InlineData("other|create|00000000000000000000000000000000|0")]
    [InlineData("rep|create|not-a-guid|0")]
    [InlineData("rep|create|00000000000000000000000000000000|NaN")]
    public void Rejects_malformed_ids(string s) => Assert.False(CustomIds.TryParse(s, out _, out _, out _));

    [Fact]
    public void Stays_within_discord_100_char_limit() =>
        Assert.InRange(CustomIds.Build(CustomIds.StillOpen, Guid.NewGuid(), int.MaxValue).Length, 1, 100);
}
