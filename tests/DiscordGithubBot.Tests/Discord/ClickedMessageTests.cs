using Discord;
using DiscordGithubBot.Discord;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DiscordGithubBot.Tests.Discord;

public class ClickedMessageTests
{
    private static readonly MessageComponent Answer = OutcomeRenderer.RenderText("✅ Created #686");

    /// <summary>
    /// The click was acknowledged by turning the clicked message into a "working on it" note; a follow-up
    /// would leave that note stranded above a second message, so the answer edits the note instead.
    /// </summary>
    [Fact]
    public async Task The_answer_replaces_the_clicked_message_instead_of_posting_a_new_one()
    {
        var interaction = Substitute.For<IComponentInteraction>();
        Action<MessageProperties>? edit = null;
        interaction.ModifyOriginalResponseAsync(Arg.Do<Action<MessageProperties>>(f => edit = f), Arg.Any<RequestOptions>())
            .Returns(Substitute.For<IUserMessage>());

        await ClickedMessage.ReplaceAsync(interaction, Answer, NullLogger.Instance);

        Assert.NotNull(edit);
        var properties = new MessageProperties();
        edit(properties);
        Assert.Same(Answer, properties.Components.Value);
        // Discord.Net does not add the Components V2 flag to an edit by itself.
        Assert.Equal(MessageFlags.ComponentsV2, properties.Flags.Value);
        Assert.False(properties.Content.IsSpecified);

        await interaction.DidNotReceiveWithAnyArgs().FollowupAsync();
    }

    /// <summary>The reporter may have dismissed the note meanwhile; the answer must still reach them.</summary>
    [Fact]
    public async Task An_answer_that_cannot_replace_the_clicked_message_is_posted_as_a_new_ephemeral_one()
    {
        var interaction = Substitute.For<IComponentInteraction>();
        interaction.ModifyOriginalResponseAsync(Arg.Any<Action<MessageProperties>>(), Arg.Any<RequestOptions>())
            .ThrowsAsync(new InvalidOperationException("Unknown Message"));

        await ClickedMessage.ReplaceAsync(interaction, Answer, NullLogger.Instance);

        await interaction.Received(1).FollowupAsync(
            Arg.Any<string>(), Arg.Any<Embed[]>(), Arg.Any<bool>(), ephemeral: true, Arg.Any<AllowedMentions>(),
            components: Answer, Arg.Any<Embed>(), Arg.Any<RequestOptions>(), Arg.Any<PollProperties>(),
            Arg.Any<MessageFlags>());
    }
}
