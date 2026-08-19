using System.Text;
using DiscordGithubBot.GitHub;

namespace DiscordGithubBot.Pipeline;

/// <summary>
/// Builds the markdown body posted to GitHub for a report: the normalized draft, an optional
/// regression reference, an optional screenshot gallery, an optional note about screenshots that
/// failed to upload, and a footer crediting the Discord reporter and the server they reported from.
/// A hidden <see cref="MetaMarker"/> sits between the draft and everything appended to it.
/// </summary>
public static class IssueBodyComposer
{
    /// <summary>
    /// Separates the reporter's own words from everything this bot appends to them. Everything before
    /// the first occurrence is the report; everything from it on is generated boilerplate. An HTML
    /// comment is invisible in GitHub's rendered markdown, so the marker costs the reader nothing —
    /// and <see cref="IssueSyncService"/> uses it to keep the boilerplate out of issue embeddings,
    /// which would otherwise make every bot-created issue look alike.
    /// <para>
    /// A composed body contains this marker exactly once, and it is always the one
    /// <see cref="Compose"/> emits: any literal marker inside the draft is stripped first. The
    /// invariant therefore holds by construction rather than by emission order — a reporter who pastes
    /// the marker into their report cannot move the cut, and so cannot hide the rest of their own text
    /// from the embedding, the content hash and the duplicate judge's excerpt.
    /// </para>
    /// </summary>
    public const string MetaMarker = "<!-- discord-gh-issue-bot:meta -->";

    /// <summary>Composes the body of a new GitHub issue.</summary>
    /// <param name="draftBody">The normalized report text.</param>
    /// <param name="reporterDisplayName">Discord display name shown in the footer.</param>
    /// <param name="guildName">Discord server the report came from; omitted from the footer when blank.</param>
    /// <param name="images">Screenshots that uploaded successfully; rendered as a gallery.</param>
    /// <param name="failedUploads">File names of screenshots that could not be uploaded.</param>
    /// <param name="regressionOfIssueNumber">Issue this may be a regression of, if any.</param>
    public static string ComposeIssueBody(
        string draftBody, string reporterDisplayName, string guildName,
        IReadOnlyList<UploadedImage> images, IReadOnlyList<string> failedUploads,
        int? regressionOfIssueNumber) =>
        Compose(draftBody, reporterDisplayName, guildName, images, failedUploads, regressionOfIssueNumber);

    /// <summary>
    /// Composes the body of a comment added to an existing issue. The first parameter is what the
    /// report *adds* to that issue, not the report itself — the issue already says the rest. Empty
    /// means the report added nothing, which composes to the attribution line alone. The footer verb
    /// changes with it: a comment records that someone recreated or experienced the issue, not that
    /// they created it.
    /// </summary>
    public static string ComposeCommentBody(
        string additionalInfo, string reporterDisplayName, string guildName,
        IReadOnlyList<UploadedImage> images, IReadOnlyList<string> failedUploads) =>
        Compose(additionalInfo, reporterDisplayName, guildName, images, failedUploads,
            regressionOfIssueNumber: null, FooterVerb.RecreatedExperienced);

    /// <summary>How the footer credits the reporter: issues are created, comments recreate/experience.</summary>
    private enum FooterVerb { Created, RecreatedExperienced }

    private static string Compose(
        string draftBody, string reporterDisplayName, string guildName,
        IReadOnlyList<UploadedImage> images, IReadOnlyList<string> failedUploads,
        int? regressionOfIssueNumber, FooterVerb verb = FooterVerb.Created)
    {
        var sb = new StringBuilder();

        // The marker means "the bot's words start here", so the draft is not allowed to contain one:
        // the reporter's text is attacker-chosen, and a pasted marker would otherwise cut the body
        // short of everything after it. Stripped before the trim, so a draft that is nothing but a
        // marker still counts as empty.
        var draft = draftBody.Replace(MetaMarker, "").Trim();
        if (draft.Length > 0) AppendBlock(sb, draft);

        // Unconditional, and ahead of every appended block including the regression line: the footer
        // below is itself unconditional, so "only mark bodies that have boilerplate" would be a branch
        // that is always taken. A body that is nothing but boilerplate is marked at position zero,
        // which reads as "no reporter text here" — exactly what it is.
        AppendBlock(sb, MetaMarker);

        if (regressionOfIssueNumber is { } number)
            AppendBlock(sb, $"Possible regression of #{number}.");

        if (images.Count > 0)
        {
            AppendBlock(sb, "### Screenshots");
            foreach (var image in images)
                sb.Append('\n').Append($"![{Escape(image.FileName)}]({image.Url})");
        }

        if (failedUploads.Count > 0)
            AppendBlock(
                sb,
                "> [!NOTE]\n> Screenshot upload failed for: " +
                $"{string.Join(", ", failedUploads.Select(Escape))}.");

        AppendBlock(sb, Footer(reporterDisplayName, guildName, verb));

        return sb.ToString();
    }

    /// <summary>
    /// The attribution line: who filed the report, and which Discord server they filed it from. A server
    /// name we could not read leaves the reporter credited on their own rather than pointing at an empty
    /// server, so the footer always names someone.
    /// </summary>
    private static string Footer(string reporterDisplayName, string guildName, FooterVerb verb)
    {
        var reporter = Escape(reporterDisplayName);
        var server = Escape(guildName);
        var action = verb == FooterVerb.Created ? "Created" : "Recreated/experienced";

        return server.Length == 0
            ? $"---\n_{action} by **{reporter}** via Discord._"
            : $"---\n_{action} by **{reporter}** in Discord server **{server}**._";
    }

    /// <summary>
    /// Characters that would let interpolated text escape the markdown built around it: out of an image
    /// link, out of a code span, or into raw HTML. The backslash is first and is not optional — without
    /// it, an attacker-supplied backslash is emitted raw in front of the one we prepend, the pair renders
    /// as a single literal backslash, and the character it was meant to neutralize is armed again.
    /// </summary>
    private static readonly char[] MarkdownSpecials = ['\\', '[', ']', '(', ')', '`', '<'];

    /// <summary>
    /// Escapes text the bot did not author before it is interpolated into markdown. Discord file names,
    /// display names and server names are attacker-chosen — a screenshot called <c>x](http://evil)![</c>
    /// otherwise rewrites the image link built around it. The draft body is deliberately left alone: it is the model's own
    /// markdown and is meant to render. Image URLs are left alone too; they come from our own upload step,
    /// and escaping them would corrupt legitimate links.
    /// </summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (c is '\r' or '\n') { sb.Append(' '); continue; }
            if (MarkdownSpecials.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>Appends a block, separated from anything already written by one blank line.</summary>
    private static void AppendBlock(StringBuilder sb, string block)
    {
        if (sb.Length > 0) sb.Append("\n\n");
        sb.Append(block);
    }
}
