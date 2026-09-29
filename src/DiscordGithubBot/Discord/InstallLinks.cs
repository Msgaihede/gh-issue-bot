using System.Globalization;

namespace DiscordGithubBot.Discord;

/// <summary>Discord OAuth2 install links for this application.</summary>
public static class InstallLinks
{
    /// <summary>
    /// Adds the application to the clicking user's own account (<c>integration_type=1</c>). A user install
    /// needs only the <c>applications.commands</c> scope — no bot user joins anything — and it only works
    /// once "User Install" is enabled under Installation in the Discord Developer Portal.
    /// </summary>
    public static string UserInstall(ulong applicationId) =>
        "https://discord.com/oauth2/authorize?client_id=" +
        applicationId.ToString(CultureInfo.InvariantCulture) +
        "&integration_type=1&scope=applications.commands";
}
