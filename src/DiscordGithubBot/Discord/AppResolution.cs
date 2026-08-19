using Discord;
using DiscordGithubBot.Configuration;

namespace DiscordGithubBot.Discord;

/// <summary>
/// Decides which configured app a command applies to. Pulled out of the interaction module because it is
/// the one piece of branching in the Discord layer worth testing on its own: a guild can host one app
/// (naming it is then noise), several apps (the reporter must pick one), or none at all.
/// </summary>
public static class AppResolution
{
    private const string NoAppConfigured = "No app is configured for this server.";

    /// <summary>
    /// Decides how a report command opens its modal. Exactly one part of the result is meaningful:
    /// the single app when the guild has one, the list to offer as a dropdown when it has several,
    /// or the message to show the reporter when it has none — or when the apps cannot be offered as
    /// a dropdown at all. The capacity guards exist because a Discord select menu holds at most
    /// <see cref="SelectMenuBuilder.MaxOptionCount"/> options of at most 100 characters each;
    /// exceeding either would throw while building the modal, turning every report in the guild
    /// into a bare apology instead of one message that names the actual problem.
    /// </summary>
    public static (AppConfig? App, IReadOnlyList<AppConfig>? Choices, string? Error) PlanModal(
        IReadOnlyList<AppConfig> guildApps) =>
        guildApps.Count switch
        {
            0 => (null, null, NoAppConfigured),
            1 => (guildApps[0], null, null),
            > SelectMenuBuilder.MaxOptionCount => (null, null,
                "More apps are configured here than the app dropdown can hold — please tell an admin."),
            _ when guildApps.Any(TooBigForDropdown) => (null, null,
                "A configured app name or repository is too long for the app dropdown — please tell an admin."),
            _ => (null, guildApps, null),
        };

    private static bool TooBigForDropdown(AppConfig app) =>
        app.Name.Length > SelectMenuOptionBuilder.MaxSelectLabelLength ||
        app.Repo.Length > SelectMenuOptionBuilder.MaxSelectValueLength;

    /// <summary>
    /// The repository a submitted report modal names: the custom id's own segment, unless that is
    /// the pick-inside-the-modal placeholder — then the dropdown's value, or "" when nothing usable
    /// arrived with the submit.
    /// </summary>
    public static string PickedRepo(string repoToken, string? selectValue) =>
        repoToken != ReportModal.PickAppToken ? repoToken : selectValue ?? "";

    /// <param name="guildApps">Apps configured for the guild the command came from.</param>
    /// <param name="appName">The optional <c>app</c> option the reporter typed.</param>
    /// <returns>Either the resolved app or a message to show the reporter — never both, never neither.</returns>
    public static (AppConfig? App, string? Error) Resolve(IReadOnlyList<AppConfig> guildApps, string? appName)
    {
        if (guildApps.Count == 0) return (null, NoAppConfigured);

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
