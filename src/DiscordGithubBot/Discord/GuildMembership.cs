using Discord;
using Discord.WebSocket;

namespace DiscordGithubBot.Discord;

/// <summary>Whether a Discord user is a member of a server. Throws when Discord cannot say.</summary>
public interface IGuildMembership
{
    Task<bool> IsMemberAsync(ulong guildId, ulong userId, CancellationToken ct);
}

/// <summary>
/// Asks Discord's REST API rather than the gateway cache: the bot runs with the <c>Guilds</c> intent only,
/// so its member cache holds whoever happened to use a command in a server and never hears that they left
/// or were banned. Works only for servers the bot is in — which every configured server is, since that is
/// where it announces.
/// </summary>
public sealed class DiscordGuildMembership(DiscordSocketClient client) : IGuildMembership
{
    public async Task<bool> IsMemberAsync(ulong guildId, ulong userId, CancellationToken ct) =>
        await client.Rest.GetGuildUserAsync(guildId, userId, new RequestOptions { CancelToken = ct }) is not null;
}
