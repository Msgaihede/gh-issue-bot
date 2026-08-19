using System.Globalization;

namespace DiscordGithubBot.Discord;

/// <summary>
/// The one place where interaction custom ids are written and read. Every button, select menu and the
/// modal route through this codec, so a click can be resolved without any server-side session state:
/// the id carries the action, the pending report it belongs to and the issue number it acts on.
/// Discord caps custom ids at 100 characters — <c>rep|stillopen|{32 hex}|{int}</c> fits in 57.
/// </summary>
public static class CustomIds
{
    public const string Prefix = "rep";
    public const string Create = "create";
    public const string Cancel = "cancel";
    public const string Comment = "comment";
    public const string Draft = "draft";
    public const string StillOpen = "stillopen";
    public const string Fixed = "fixed";
    public const string Pick = "pick";

    private const int Segments = 4;

    /// <param name="issueNumber">
    /// Meaning depends on the action: the issue to comment on, the issue a regression refers back to,
    /// or 0 when the action needs no issue.
    /// </param>
    public static string Build(string action, Guid id, int issueNumber = 0) =>
        $"{Prefix}|{action}|{id:N}|{issueNumber}";

    /// <summary>
    /// Reads an id built by <see cref="Build"/>. Anything else — a foreign component, a truncated id, a
    /// hand-edited one — is rejected rather than half-parsed, because the caller acts on a real report.
    /// </summary>
    public static bool TryParse(string customId, out string action, out Guid id, out int issueNumber)
    {
        action = "";
        id = Guid.Empty;
        issueNumber = 0;

        if (string.IsNullOrEmpty(customId)) return false;

        var parts = customId.Split('|');
        if (parts.Length != Segments) return false;
        if (parts[0] != Prefix || parts[1].Length == 0) return false;
        if (!Guid.TryParseExact(parts[2], "N", out var parsedId)) return false;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumber))
            return false;

        action = parts[1];
        id = parsedId;
        issueNumber = parsedNumber;
        return true;
    }
}
