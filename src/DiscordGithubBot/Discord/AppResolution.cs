using DiscordGithubBot.Configuration;

namespace DiscordGithubBot.Discord;

/// <summary>
/// Decides which configured app a command applies to. Pulled out of the interaction module because it is
/// the one piece of branching in the Discord layer worth testing on its own: a guild can host one app
/// (naming it is then noise), several apps (the reporter must pick one), or none at all.
/// </summary>
public static class AppResolution
{
    /// <summary>
    /// Decides how a report command opens its modal. Exactly one part of the result is meaningful:
    /// the single app when the guild has one, the list to offer as a dropdown when it has several,
    /// or the message to show the reporter when it has none.
    /// </summary>
    public static (AppConfig? App, IReadOnlyList<AppConfig>? Choices, string? Error) PlanModal(
        IReadOnlyList<AppConfig> guildApps) =>
        guildApps.Count switch
        {
            0 => (null, null, "No app is configured for this server."),
            1 => (guildApps[0], null, null),
            _ => (null, guildApps, null),
        };

    /// <param name="guildApps">Apps configured for the guild the command came from.</param>
    /// <param name="appName">The optional <c>app</c> option the reporter typed.</param>
    /// <returns>Either the resolved app or a message to show the reporter — never both, never neither.</returns>
    public static (AppConfig? App, string? Error) Resolve(IReadOnlyList<AppConfig> guildApps, string? appName)
    {
        if (guildApps.Count == 0) return (null, "No app is configured for this server.");

        if (!string.IsNullOrWhiteSpace(appName))
        {
            var wanted = appName.Trim();
            var match = guildApps.FirstOrDefault(a => string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase));

            return match is not null
                ? (match, null)
                : (null, $"There is no app called \"{wanted}\" here. Configured apps: {Names(guildApps)}.");
        }

        return guildApps.Count == 1
            ? (guildApps[0], null)
            : (null, $"Several apps are configured here — run the command again with app: one of {Names(guildApps)}.");
    }

    private static string Names(IReadOnlyList<AppConfig> apps) => string.Join(", ", apps.Select(a => a.Name));
}
