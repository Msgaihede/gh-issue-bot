using DiscordGithubBot.Configuration;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Discord;

/// <summary>The apps a command may use; when there are none, <paramref name="Error"/> says why.</summary>
public sealed record AppAccessResult(IReadOnlyList<AppConfig> Apps, string? Error);

/// <summary>
/// Decides which configured apps a command may use, given where it ran and who ran it. A server listed in
/// some app's <see cref="AppConfig.GuildIds"/> sees exactly those apps. Anywhere else — a DM, a group DM,
/// or a server nobody configured, all reachable through a user install — the user sees the apps of the
/// configured servers they are a member of, so installing the bot does not open every repository to
/// anyone who finds it (decision 101). Used when <c>/issue</c> opens its modal, when the modal is
/// submitted, and by <c>/list-issues</c>.
/// </summary>
public sealed class AppAccess(BotOptions options, IGuildMembership membership, ILogger<AppAccess> logger)
{
    internal const string NotAMember =
        "I can only do that for apps whose Discord server you're a member of, and you're not in any of them.";
    internal const string LookupFailed =
        "I couldn't check which servers you're in just now. Please try again in a moment.";

    /// <summary>
    /// How long the membership lookups may take together. <c>/issue</c> must answer with its modal inside
    /// Discord's three-second window, and a modal cannot follow a defer, so a lookup still running at the
    /// deadline counts as unknown rather than failing the whole interaction.
    /// </summary>
    internal TimeSpan LookupTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<AppAccessResult> ForAsync(ulong? guildId, ulong userId, CancellationToken ct = default)
    {
        var here = options.AppsForGuild(guildId);
        if (here.Count > 0) return new(here, null);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(LookupTimeout);

        // One lookup per server, all at once: several apps may share a server.
        var answers = await Task.WhenAll(options.Apps.SelectMany(a => a.GuildIds).Distinct()
            .Select(async id => (Guild: id, IsMember: await IsMemberAsync(id, userId, deadline.Token))));

        var memberOf = answers.Where(a => a.IsMember == true).Select(a => a.Guild).ToHashSet();
        var apps = options.Apps.Where(a => a.GuildIds.Any(memberOf.Contains)).ToList();
        if (apps.Count > 0) return new(apps, null);

        // "You're not in any of them" would be wrong if a server could not be checked.
        return new([], answers.Any(a => a.IsMember is null) ? LookupFailed : NotAMember);
    }

    /// <returns>Whether the user is a member, or null when Discord did not answer in time or at all.</returns>
    private async Task<bool?> IsMemberAsync(ulong guildId, ulong userId, CancellationToken ct)
    {
        try
        {
            // WaitAsync holds the deadline even if the lookup ignores its token.
            return await membership.IsMemberAsync(guildId, userId, ct).WaitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check whether user {UserId} is a member of server {GuildId}.",
                userId, guildId);
            return null;
        }
    }
}
