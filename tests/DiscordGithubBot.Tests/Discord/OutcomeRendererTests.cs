using Discord;
using DiscordGithubBot.Ai;
using DiscordGithubBot.Data;
using DiscordGithubBot.Discord;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;

namespace DiscordGithubBot.Tests.Discord;

/// <summary>
/// The renderers are the only logic in the Discord layer: they decide what a reporter reads and which
/// custom id each button carries, which is what the component handlers route on.
/// </summary>
public class OutcomeRendererTests
{
    private static readonly Guid PendingId = Guid.Parse("0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9");

    private static IEnumerable<IMessageComponent> Flatten(MessageComponent message) =>
        Flatten(message.Components);

    private static IEnumerable<IMessageComponent> Flatten(IEnumerable<IMessageComponent> components) =>
        components.SelectMany(c => c switch
        {
            ContainerComponent container => Flatten(container.Components).Prepend(c),
            ActionRowComponent row => Flatten(row.Components).Prepend(c),
            _ => [c],
        });

    private static string Text(MessageComponent message) =>
        string.Join("\n", Flatten(message).OfType<TextDisplayComponent>().Select(t => t.Content));

    private static IReadOnlyList<ButtonComponent> Buttons(MessageComponent message) =>
        Flatten(message).OfType<ButtonComponent>().ToList();

    private static GitHubIssue Issue(int number) =>
        new(number, $"Issue {number}", "", "open", DateTime.UtcNow, null, $"https://github.com/acme/mira/issues/{number}");

    [Fact]
    public void Open_match_offers_to_comment_on_that_issue()
    {
        var match = new CandidateIssue(7, "Crash on launch", "open", "https://github.com/acme/mira/issues/7");

        var message = OutcomeRenderer.RenderMatch(match, PendingId);

        Assert.Contains("[#7 Crash on launch](https://github.com/acme/mira/issues/7)", Text(message));
        Assert.Equal(
            [CustomIds.Build(CustomIds.Comment, PendingId, 7), CustomIds.Build(CustomIds.Draft, PendingId)],
            Buttons(message).Select(b => b.CustomId));
    }

    [Fact]
    public void Closed_match_asks_whether_it_is_still_happening()
    {
        var match = new CandidateIssue(7, "Crash on launch", "closed", "https://github.com/acme/mira/issues/7");

        var message = OutcomeRenderer.RenderMatch(match, PendingId);

        Assert.Contains("Is it still happening", Text(message));
        Assert.Equal(
            [CustomIds.Build(CustomIds.StillOpen, PendingId, 7), CustomIds.Build(CustomIds.Fixed, PendingId, 7)],
            Buttons(message).Select(b => b.CustomId));
    }

    [Fact]
    public void Uncertain_lists_the_candidates_and_an_escape_hatch()
    {
        var candidates = new List<CandidateIssue>
        {
            new(7, "Crash on launch", "open", "https://github.com/acme/mira/issues/7"),
            new(9, "Crash on resume", "closed", "https://github.com/acme/mira/issues/9"),
        };

        var message = OutcomeRenderer.Render(
            new ReportOutcome(ReportOutcomeKind.Uncertain, PendingId, new IssueDraft("t", "b"), null, candidates));

        var menu = Assert.Single(Flatten(message).OfType<SelectMenuComponent>());
        Assert.Equal(CustomIds.Build(CustomIds.Pick, PendingId), menu.CustomId);
        Assert.Equal(["7", "9"], menu.Options.Select(o => o.Value));
        Assert.Equal(CustomIds.Build(CustomIds.Draft, PendingId), Assert.Single(Buttons(message)).CustomId);
    }

    [Fact]
    public void No_match_previews_the_draft_with_create_and_cancel()
    {
        var message = OutcomeRenderer.Render(new ReportOutcome(
            ReportOutcomeKind.NoMatch, PendingId, new IssueDraft("App crashes", "Steps..."), null, []));

        Assert.Contains("**App crashes**", Text(message));
        Assert.Equal(
            [CustomIds.Build(CustomIds.Create, PendingId), CustomIds.Build(CustomIds.Cancel, PendingId)],
            Buttons(message).Select(b => b.CustomId));
        Assert.Equal([ButtonStyle.Success, ButtonStyle.Danger], Buttons(message).Select(b => b.Style));
    }

    [Fact]
    public void A_regression_draft_carries_the_old_issue_into_the_create_button()
    {
        var message = OutcomeRenderer.RenderDraftPreview(new IssueDraft("App crashes", "Steps..."), PendingId, 7);

        Assert.Equal(CustomIds.Build(CustomIds.Create, PendingId, 7), Buttons(message)[0].CustomId);
    }

    [Fact]
    public void A_notice_is_rendered_into_the_message_rather_than_passed_as_content()
    {
        var message = OutcomeRenderer.Render(
            new ReportOutcome(ReportOutcomeKind.NoMatch, PendingId, new IssueDraft("t", "b"), null, []),
            "⚠️ Skipped: notes.txt");

        Assert.Contains("⚠️ Skipped: notes.txt", Text(message));
    }

    [Fact]
    public void The_issue_list_shows_25_issues_and_counts_the_rest()
    {
        var message = OutcomeRenderer.RenderIssueList("mira", [.. Enumerable.Range(1, 30).Select(Issue)]);

        var text = Text(message);
        Assert.Contains("**Open issues — mira**", text);
        Assert.Contains("- [#25 Issue 25](https://github.com/acme/mira/issues/25)", text);
        Assert.DoesNotContain("#26 Issue 26", text);
        Assert.Contains("+5 more on GitHub", text);
    }

    [Fact]
    public void The_issue_list_drops_whole_lines_rather_than_cutting_a_link_in_half()
    {
        var wordy = Enumerable.Range(1, 25)
            .Select(n => new GitHubIssue(
                n, new string('x', 300), "", "open", DateTime.UtcNow, null,
                $"https://github.com/acme/mira/issues/{n}"))
            .ToList();

        var text = Text(OutcomeRenderer.RenderIssueList("mira", wordy));

        Assert.True(text.Length <= 3000, $"issue list grew to {text.Length} characters");
        Assert.Matches(@"\+\d+ more on GitHub", text);
        Assert.All(
            text.Split('\n').Where(l => l.StartsWith("- ")),
            line => Assert.EndsWith(")", line));
    }

    [Fact]
    public void An_empty_issue_list_says_so()
    {
        Assert.Contains("No open issues", Text(OutcomeRenderer.RenderIssueList("mira", [])));
    }

    [Fact]
    public void The_announcement_names_the_app_the_issue_and_the_reporter()
    {
        var message = OutcomeRenderer.RenderAnnouncement(
            new CreatedIssueResult(12, "App crashes", "https://github.com/acme/mira/issues/12"),
            "mira", "Sam", ReportType.Bug);

        var text = Text(message);
        Assert.Contains("**New bug report for mira**", text);
        Assert.Contains("[#12 App crashes](https://github.com/acme/mira/issues/12)", text);
        Assert.Contains("Reported by Sam via Discord", text);
    }

    [Fact]
    public void Titles_cannot_break_out_of_their_markdown_link()
    {
        var message = OutcomeRenderer.RenderAnnouncement(
            new CreatedIssueResult(12, "Crash in [beta]\nbuild", "https://github.com/acme/mira/issues/12"),
            "mira", "Sam", ReportType.Feature);

        Assert.Contains(@"[#12 Crash in \[beta\] build](https://github.com/acme/mira/issues/12)", Text(message));
    }

    [Fact]
    public void A_runaway_draft_title_is_cut_to_its_own_cap()
    {
        var message = OutcomeRenderer.RenderDraftPreview(
            new IssueDraft(new string('t', 500), "body"), PendingId);

        var text = Text(message);
        Assert.DoesNotContain(new string('t', 200), text);
        Assert.Contains("body", text); // the body still made it past the title
    }

    [Fact]
    public void A_runaway_skipped_files_notice_is_cut_to_its_own_cap()
    {
        var message = OutcomeRenderer.Render(
            new ReportOutcome(ReportOutcomeKind.NoMatch, PendingId, new IssueDraft("t", "b"), null, []),
            "⚠️ Skipped: " + new string('f', 2000));

        var text = Text(message);
        Assert.True(text.Length <= 3800, $"draft preview grew to {text.Length} characters");
        Assert.Contains("⚠️ Skipped:", text);
        Assert.Contains("**t**", text); // the draft is still visible under the notice
    }

    [Fact]
    public void No_rendered_message_can_exceed_the_whole_message_budget()
    {
        var huge = new string('x', 5000);
        var candidates = new List<CandidateIssue> { new(7, huge, "open", "https://github.com/acme/mira/issues/7") };

        MessageComponent[] messages =
        [
            OutcomeRenderer.RenderDraftPreview(new IssueDraft(huge, huge), PendingId, notice: huge),
            OutcomeRenderer.RenderMatch(candidates[0], PendingId, huge),
            OutcomeRenderer.RenderMatch(
                new CandidateIssue(9, huge, "closed", "https://github.com/acme/mira/issues/9"), PendingId, huge),
            OutcomeRenderer.Render(
                new ReportOutcome(ReportOutcomeKind.Uncertain, PendingId, new IssueDraft(huge, huge), null, candidates),
                huge),
            OutcomeRenderer.RenderAnnouncement(
                new CreatedIssueResult(12, huge, "https://github.com/acme/mira/issues/12"), huge, huge, ReportType.Bug),
        ];

        Assert.All(messages, m => Assert.True(
            Text(m).Length <= 3800, $"a rendered message grew to {Text(m).Length} characters"));
    }

    [Fact]
    public void The_working_placeholder_has_no_buttons_left_to_click()
    {
        var message = OutcomeRenderer.RenderWorking();

        Assert.Empty(Buttons(message));
        Assert.Contains("Working on it", Text(message));
    }
}
