using System.Text;
using DiscordGithubBot.GitHub;

namespace DiscordGithubBot.Pipeline;

/// <summary>
/// Builds the markdown body posted to GitHub for a report: the normalized draft, an optional
/// regression reference, an optional screenshot gallery, an optional note about screenshots that
/// failed to upload, and a footer crediting the Discord reporter.
/// </summary>
public static class IssueBodyComposer
{
    /// <summary>Composes the body of a new GitHub issue.</summary>
    /// <param name="draftBody">The normalized report text.</param>
    /// <param name="reporterDisplayName">Discord display name shown in the footer.</param>
    /// <param name="images">Screenshots that uploaded successfully; rendered as a gallery.</param>
    /// <param name="failedUploads">File names of screenshots that could not be uploaded.</param>
    /// <param name="regressionOfIssueNumber">Issue this may be a regression of, if any.</param>
    public static string ComposeIssueBody(
        string draftBody, string reporterDisplayName,
        IReadOnlyList<UploadedImage> images, IReadOnlyList<string> failedUploads,
        int? regressionOfIssueNumber) =>
        Compose(draftBody, reporterDisplayName, images, failedUploads, regressionOfIssueNumber);

    /// <summary>
    /// Composes the body of a comment added to an existing issue: identical to
    /// <see cref="ComposeIssueBody"/> minus the regression reference.
    /// </summary>
    public static string ComposeCommentBody(
        string draftBody, string reporterDisplayName,
        IReadOnlyList<UploadedImage> images, IReadOnlyList<string> failedUploads) =>
        Compose(draftBody, reporterDisplayName, images, failedUploads, regressionOfIssueNumber: null);

    private static string Compose(
        string draftBody, string reporterDisplayName,
        IReadOnlyList<UploadedImage> images, IReadOnlyList<string> failedUploads,
        int? regressionOfIssueNumber)
    {
        var sb = new StringBuilder();

        var draft = draftBody.Trim();
        if (draft.Length > 0) AppendBlock(sb, draft);

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

        AppendBlock(sb, $"---\n_Reported by **{Escape(reporterDisplayName)}** via Discord._");

        return sb.ToString();
    }

    /// <summary>
    /// Characters that would let interpolated text escape the markdown built around it: out of an image
    /// link, out of a code span, or into raw HTML.
    /// </summary>
    private static readonly char[] MarkdownSpecials = ['[', ']', '(', ')', '`', '<'];

    /// <summary>
    /// Escapes text the bot did not author before it is interpolated into markdown. Discord file names and
    /// display names are attacker-chosen — a screenshot called <c>x](http://evil)![</c> otherwise rewrites
    /// the image link built around it. The draft body is deliberately left alone: it is the model's own
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
