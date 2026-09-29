using Discord;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Discord;

/// <summary>
/// Delivers the answer to a button or select click. The click was acknowledged by turning the clicked
/// message into a "working on it" note (<see cref="OutcomeRenderer.RenderWorking"/>), so the answer edits
/// that note rather than following up: a follow-up is always a new message, which would leave the note
/// stranded above it.
/// </summary>
public static class ClickedMessage
{
    /// <summary>
    /// Replaces the clicked message with <paramref name="components"/>. If Discord refuses the edit — the
    /// reporter may have dismissed the note meanwhile — the answer goes out as a new ephemeral message
    /// instead, so it still reaches the reporter.
    /// </summary>
    public static async Task ReplaceAsync(IComponentInteraction interaction, MessageComponent components, ILogger logger)
    {
        try
        {
            // After an update (or a deferred update) the "original response" is the clicked message itself.
            await interaction.ModifyOriginalResponseAsync(message =>
            {
                message.Components = components;
                message.Flags = MessageFlags.ComponentsV2;
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not replace the clicked message; posting the answer as a new message.");
            await interaction.FollowupAsync(components: components, ephemeral: true);
        }
    }
}
