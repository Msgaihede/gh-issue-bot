using Discord;
using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;

namespace DiscordGithubBot.Discord;

/// <summary>
/// Turns pipeline results into Components V2 messages. Everything here is a pure function of its
/// arguments: the interaction module decides what to show, this decides what it looks like, and the
/// button custom ids it writes are the only contract between the two.
/// </summary>
/// <remarks>
/// A Components V2 message carries all of its text inside the payload — Discord rejects a message that
/// sets both the CV2 flag and message content — so notices and headings are rendered as text displays
/// here rather than passed as the <c>text</c> argument of a response.
/// </remarks>
public static class OutcomeRenderer
{
    /// <summary>Draft bodies are cut here; Discord caps a whole CV2 message at 4000 characters.</summary>
    private const int MaxBodyChars = 3000;

    /// <summary>
    /// Whole-message budget. Discord rejects a Components V2 message whose text exceeds 4000 characters,
    /// and every text display here is assembled from several capped parts, so the assembled result is cut
    /// once more with headroom left for the components around it. A rejected message means the reporter
    /// sees nothing at all, which is the one outcome worse than a truncated preview.
    /// </summary>
    private const int MaxMessageChars = 3800;

    /// <summary>Model-authored titles are asked to stay under 80 characters; this is the hard cut.</summary>
    private const int MaxTitleChars = 150;

    /// <summary>Cut for the skipped-attachments notice, which is as long as the file names the reporter chose.</summary>
    private const int MaxNoticeChars = 300;

    /// <summary>Discord's cap for a select option label, and a sane cut for a listed issue title.</summary>
    private const int MaxLabelChars = 100;

    /// <summary>Discord shows at most 25 select options; the issue list uses the same cut.</summary>
    private const int MaxListedIssues = 25;

    /// <summary>Room kept free in the issue list for the "+K more on GitHub" line.</summary>
    private const int MoreNoteReserve = 32;

    /// <summary>Ephemeral response for a pipeline outcome (match found / uncertain list / draft preview).</summary>
    /// <param name="notice">Optional first line, e.g. which attachments were skipped.</param>
    public static MessageComponent Render(ReportOutcome outcome, string? notice = null) => outcome.Kind switch
    {
        ReportOutcomeKind.MatchOpen or ReportOutcomeKind.MatchClosed when outcome.Match is not null =>
            RenderMatch(outcome.Match, outcome.PendingReportId, notice),
        ReportOutcomeKind.Uncertain when outcome.Candidates.Count > 0 =>
            RenderUncertain(outcome.Candidates, outcome.PendingReportId, notice),
        _ => RenderDraftPreview(
            outcome.Draft, outcome.PendingReportId,
            heading: "**No existing issue matches. Here's the draft:**", notice: notice),
    };

    /// <summary>
    /// The "we found something" flow for one candidate: an open issue offers to attach the report to it,
    /// a closed one asks whether the problem is back.
    /// </summary>
    public static MessageComponent RenderMatch(CandidateIssue match, Guid pendingId, string? notice = null) =>
        string.Equals(match.State, "open", StringComparison.OrdinalIgnoreCase)
            ? RenderMatchOpen(match, pendingId, notice)
            : RenderMatchClosed(match, pendingId, notice);

    /// <summary>Draft preview with Create/Cancel buttons; regressionOf carries into the create button's custom id.</summary>
    public static MessageComponent RenderDraftPreview(
        IssueDraft draft, Guid pendingId, int regressionOf = 0, string? heading = null, string? notice = null) =>
        Container(container => container
            .WithTextDisplay(Budgeted(
                Notice(notice) + (heading is null ? "" : heading + "\n") +
                $"**{Truncate(Inline(draft.Title), MaxTitleChars)}**\n{Truncate(draft.Body, MaxBodyChars)}"))
            .WithActionRow(row => row
                .WithButton(ButtonBuilder.CreateSuccessButton(
                    "Create issue", CustomIds.Build(CustomIds.Create, pendingId, regressionOf)))
                .WithButton(ButtonBuilder.CreateDangerButton(
                    "Cancel", CustomIds.Build(CustomIds.Cancel, pendingId)))));

    /// <summary>Public channel announcement for a created issue.</summary>
    public static MessageComponent RenderAnnouncement(
        CreatedIssueResult issue, string appName, string reporterDisplayName, ReportType type) =>
        Container(container => container.WithTextDisplay(Budgeted(
            $"**New {(type == ReportType.Bug ? "bug report" : "feature request")} for {Inline(appName)}**\n" +
            $"[#{issue.Number} {Truncate(Inline(issue.Title), MaxLabelChars)}]({issue.HtmlUrl})\n" +
            $"Reported by {Inline(reporterDisplayName)} via Discord")));

    /// <summary>Ephemeral open-issues list for /issues.</summary>
    public static MessageComponent RenderIssueList(string appName, IReadOnlyList<GitHubIssue> issues)
    {
        var heading = $"**Open issues — {Inline(appName)}**";

        // Lines are dropped whole rather than cut in half: a list that ends mid-link reads as a bug, and
        // whatever does not fit is counted into the "more on GitHub" note instead.
        var budget = MaxBodyChars - heading.Length - MoreNoteReserve;
        var lines = new List<string>();
        var used = 0;

        foreach (var issue in issues.Take(MaxListedIssues))
        {
            var line = $"- [#{issue.Number} {Truncate(Inline(issue.Title), MaxLabelChars)}]({issue.HtmlUrl})";
            if (used + line.Length + 1 > budget) break;

            lines.Add(line);
            used += line.Length + 1;
        }

        var hidden = issues.Count - lines.Count;
        if (hidden > 0) lines.Add($"+{hidden} more on GitHub");

        var body = lines.Count == 0 ? "No open issues 🎉" : string.Join("\n", lines);
        return Container(container => container.WithTextDisplay(Budgeted($"{heading}\n{body}")));
    }

    /// <summary>
    /// Replaces a clicked message while the slow work runs: the buttons go away, so the same report
    /// cannot be submitted twice from the same message.
    /// </summary>
    public static MessageComponent RenderWorking() =>
        new ComponentBuilderV2().WithTextDisplay("⏳ Working on it...").Build();

    /// <summary>Confirmation for a created issue.</summary>
    public static MessageComponent RenderCreated(CreatedIssueResult issue) =>
        Message($"✅ Created [#{issue.Number} {Truncate(Inline(issue.Title), MaxLabelChars)}]({issue.HtmlUrl})");

    /// <summary>Confirmation for a report attached to an existing issue.</summary>
    public static MessageComponent RenderCommented(CommentResult comment) =>
        Message($"💬 Added your report to [#{comment.IssueNumber}]({comment.CommentUrl})");

    /// <summary>Closing message for a reporter who confirmed a closed issue really is fixed.</summary>
    public static MessageComponent RenderFixed(string repoKey, int issueNumber) =>
        Message($"Glad it's fixed! Reference: [#{issueNumber}](https://github.com/{repoKey}/issues/{issueNumber})");

    private static MessageComponent RenderMatchOpen(CandidateIssue match, Guid pendingId, string? notice) =>
        Container(container => container
            .WithTextDisplay(Budgeted(Notice(notice) + $"**This looks like an existing issue:** {Link(match)}"))
            .WithActionRow(row => row
                .WithButton(ButtonBuilder.CreatePrimaryButton(
                    "Same issue — add my report", CustomIds.Build(CustomIds.Comment, pendingId, match.Number)))
                .WithButton(ButtonBuilder.CreateSecondaryButton(
                    "Not it — show my draft", CustomIds.Build(CustomIds.Draft, pendingId)))));

    private static MessageComponent RenderMatchClosed(CandidateIssue match, Guid pendingId, string? notice) =>
        Container(container => container
            .WithTextDisplay(Budgeted(
                Notice(notice) + $"**This looks like {Link(match)}, closed recently.** " +
                "Is it still happening in the latest version?"))
            .WithActionRow(row => row
                .WithButton(ButtonBuilder.CreatePrimaryButton(
                    "Still happening", CustomIds.Build(CustomIds.StillOpen, pendingId, match.Number)))
                .WithButton(ButtonBuilder.CreateSecondaryButton(
                    "Looks fixed", CustomIds.Build(CustomIds.Fixed, pendingId, match.Number)))));

    private static MessageComponent RenderUncertain(
        IReadOnlyList<CandidateIssue> candidates, Guid pendingId, string? notice)
    {
        var options = candidates
            .Take(MaxListedIssues)
            .Select(c => new SelectMenuOptionBuilder(
                Truncate($"#{c.Number} {Inline(c.Title)}", MaxLabelChars),
                c.Number.ToString(),
                c.State))
            .ToList();

        return Container(container => container
            .WithTextDisplay(Budgeted(
                Notice(notice) + "**This might match an existing issue.** " +
                "Pick one to attach your report, or continue with a new issue."))
            .WithActionRow(row => row.WithSelectMenu(new SelectMenuBuilder()
                .WithCustomId(CustomIds.Build(CustomIds.Pick, pendingId))
                .WithPlaceholder("Pick the matching issue")
                .WithMinValues(1)
                .WithMaxValues(1)
                .WithOptions(options)))
            .WithActionRow(row => row.WithButton(ButtonBuilder.CreateSecondaryButton(
                "None of these — new issue", CustomIds.Build(CustomIds.Draft, pendingId)))));
    }

    private static MessageComponent Message(string text) =>
        new ComponentBuilderV2().WithTextDisplay(Truncate(text, MaxBodyChars)).Build();

    private static MessageComponent Container(Action<ContainerBuilder> build)
    {
        var container = new ContainerBuilder();
        build(container);
        return new ComponentBuilderV2().WithContainer(container).Build();
    }

    private static string Link(CandidateIssue candidate) =>
        $"[#{candidate.Number} {Truncate(Inline(candidate.Title), MaxLabelChars)}]({candidate.Url})";

    private static string Notice(string? notice) =>
        string.IsNullOrWhiteSpace(notice) ? "" : Truncate(Inline(notice), MaxNoticeChars) + "\n";

    /// <summary>Last cut before the text goes into a component, after every part has had its own.</summary>
    private static string Budgeted(string text) => Truncate(text, MaxMessageChars);

    /// <summary>Keeps user- and model-authored text from breaking the surrounding Markdown link or line.</summary>
    private static string Inline(string value) =>
        value.Replace("\r", " ").Replace("\n", " ").Replace("[", "\\[").Replace("]", "\\]").Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
