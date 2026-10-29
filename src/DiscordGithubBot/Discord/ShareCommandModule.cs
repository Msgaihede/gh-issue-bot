using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Discord;

/// <summary>
/// The commands that post something for the whole channel: the install link and the "Report an issue"
/// button. Unlike <see cref="ReportInteractionModule"/> they are not global: <c>BotService</c> registers
/// this module only in the servers listed in <c>Discord:ShareCommandGuildIds</c> (decision 111), so it
/// declares no integration types or contexts — Discord only reads those on global commands. The button's
/// click is a component, not a command, and stays in <see cref="ReportInteractionModule"/>, where it
/// works wherever a posted button is pressed.
/// </summary>
public class ShareCommandModule(DiscordSocketClient client, ILogger<ShareCommandModule> logger)
    : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>
    /// Hands out the link that adds the bot to the caller's own Discord account, after which <c>/issue</c>
    /// works in any server, DM or group DM — not only in the servers the bot was added to. With
    /// <c>public</c> the link is posted for everyone in the channel (see <see cref="PostPubliclyAsync"/>).
    /// </summary>
    [SlashCommand("issue-install", "Get a link to add this bot to your own Discord account", runMode: RunMode.Sync)]
    public async Task IssueInstall(
        [Summary("public", "Post the link in this channel for everyone, not only for you")] bool isPublic = false)
    {
        var card = OutcomeRenderer.RenderInstallLink(InstallLinks.UserInstall(Context.Interaction.ApplicationId));

        if (isPublic)
            await PostPubliclyAsync(card, "the link", "Run `/issue-install` without `public` to get it for yourself.");
        else
            await RespondAsync(components: card, ephemeral: true);
    }

    /// <summary>
    /// Posts a standing "Report an issue" button in the channel. Pressing it does exactly what <c>/issue</c>
    /// does, for whoever pressed it: their apps, their rate limit, their modal (see <see cref="ReportInteractionModule.OnOpenReport"/>).
    /// </summary>
    [SlashCommand("issue-button", "Post a \"Report an issue\" button in this channel for everyone", runMode: RunMode.Sync)]
    public Task IssueButton() =>
        PostPubliclyAsync(OutcomeRenderer.RenderReportButton(), "the button", "Use `/issue` to report directly.");

    /// <summary>
    /// Shows <paramref name="card"/> to everyone in the channel. Where the bot itself may post, it posts a
    /// plain channel message and answers the interaction with nothing at all, so no "used /…" header hangs
    /// over the card (decision 109). Anywhere else (a channel it may not write to, or a server that added
    /// only the commands and not the bot) a public reply is the only way left to show it.
    /// </summary>
    /// <param name="what">What was posted, for the caller's notes ("the link").</param>
    /// <param name="alternative">What the caller can do instead when the post fails.</param>
    private async Task PostPubliclyAsync(MessageComponent card, string what, string alternative)
    {
        if (PostableChannel() is not { } channel)
        {
            await RespondAsync(components: card);
            return;
        }

        // Acknowledged first — the three-second deadline — then the deferred "thinking" note is deleted
        // once the channel message is out, which leaves the bot's message as the only trace.
        await DeferAsync(ephemeral: true);

        try
        {
            await channel.SendMessageAsync(components: card, flags: MessageFlags.ComponentsV2);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not post {What} in channel {ChannelId}.", what, channel.Id);
            await FollowupAsync($"I couldn't post {what} in this channel. {alternative}", ephemeral: true);
            return;
        }

        try
        {
            await DeleteOriginalResponseAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete the deferred response after posting {What}.", what);
            await FollowupAsync($"Posted {what} in this channel.", ephemeral: true);
        }
    }

    /// <summary>
    /// The channel the command came from, when the bot itself may post there; otherwise null. Read from
    /// the gateway cache only — the command has three seconds — and the cache knows every server
    /// the bot is in (the <c>Guilds</c> intent), which is exactly the question. A thread's permissions are
    /// its parent's, with "send in threads" in place of "send".
    /// </summary>
    private IMessageChannel? PostableChannel()
    {
        if (Context.Interaction.GuildId is not { } guildId || Context.Interaction.ChannelId is not { } channelId)
            return null;
        if (client.GetGuild(guildId) is not { CurrentUser: { } self } guild) return null;

        var channel = guild.GetChannel(channelId);
        var thread = channel as SocketThreadChannel;
        if (channel is not IMessageChannel messageChannel || (thread?.ParentChannel ?? channel) is not { } source)
            return null;

        var permissions = self.GetPermissions(source);
        var canSend = thread is null ? permissions.SendMessages : permissions.SendMessagesInThreads;
        return permissions.ViewChannel && canSend ? messageChannel : null;
    }
}
