using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;

namespace DiscordGithubBot.Tests.Pipeline;

public class IssueBodyComposerTests
{
    [Fact]
    public void Minimal_body_is_the_draft_plus_the_attribution_footer()
    {
        var body = IssueBodyComposer.ComposeIssueBody("The body.", "markus", "Acme HQ", [], []);
        Assert.StartsWith("The body.", body);
        Assert.Contains("_Created by **markus** in Discord server **Acme HQ**._", body);
        Assert.DoesNotContain("Screenshots", body);
        Assert.DoesNotContain("upload failed", body);
    }

    [Fact]
    public void A_hidden_marker_separates_the_draft_from_everything_appended_to_it()
    {
        var body = IssueBodyComposer.ComposeIssueBody(
            "The body.", "markus", "Acme HQ",
            [new UploadedImage("a.png", "https://x/a")], ["b.png"]);

        var marker = body.IndexOf(IssueBodyComposer.MetaMarker, StringComparison.Ordinal);
        Assert.True(marker > 0);
        Assert.Contains("The body.", body[..marker]);

        // Every appended block is behind the marker.
        foreach (var boilerplate in new[] { "### Screenshots", "> [!NOTE]", "_Created by" })
            Assert.Contains(boilerplate, body[marker..]);
    }

    [Fact]
    public void The_marker_is_emitted_even_when_the_body_is_only_the_footer()
    {
        // The footer is unconditional, so there is no such thing as a composed body without
        // boilerplate: the marker is emitted unconditionally rather than behind a branch that is
        // always taken. An empty draft puts it at position zero, meaning "no reporter text here".
        var body = IssueBodyComposer.ComposeIssueBody("   ", "markus", "Acme HQ", [], []);

        Assert.StartsWith(IssueBodyComposer.MetaMarker, body);
    }

    [Fact]
    public void A_marker_pasted_into_the_draft_is_stripped_rather_than_honoured()
    {
        // The reporter's text is attacker-chosen. A literal marker inside it would otherwise be the
        // *first* one in the composed body, moving the cut IssueSyncService makes and hiding the rest
        // of the report from the embedding, the content hash and the judge's excerpt.
        var draft = $"Before.\n\n{IssueBodyComposer.MetaMarker}\n\nAfter.";

        var body = IssueBodyComposer.ComposeIssueBody(draft, "markus", "Acme HQ", [], []);

        Assert.Equal(1, CountMarkers(body));
        var marker = body.IndexOf(IssueBodyComposer.MetaMarker, StringComparison.Ordinal);
        Assert.Contains("Before.", body[..marker]);
        Assert.Contains("After.", body[..marker]);
        Assert.Contains("_Created by", body[marker..]);
    }

    [Fact]
    public void A_draft_that_is_nothing_but_a_marker_composes_as_an_empty_draft()
    {
        var body = IssueBodyComposer.ComposeCommentBody(
            $"  {IssueBodyComposer.MetaMarker}  ", "markus", "Acme HQ", [], []);

        Assert.StartsWith(IssueBodyComposer.MetaMarker, body);
        Assert.Equal(1, CountMarkers(body));
    }

    private static int CountMarkers(string body)
    {
        var count = 0;
        for (var i = body.IndexOf(IssueBodyComposer.MetaMarker, StringComparison.Ordinal); i >= 0;
             i = body.IndexOf(IssueBodyComposer.MetaMarker, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void Comment_bodies_are_marked_too()
    {
        var body = IssueBodyComposer.ComposeCommentBody("The new detail.", "markus", "Acme HQ", [], []);

        var marker = body.IndexOf(IssueBodyComposer.MetaMarker, StringComparison.Ordinal);
        Assert.True(marker > 0);
        Assert.Contains("_Also reported by", body[marker..]);
    }

    [Fact]
    public void Comment_footer_says_also_reported_not_created()
    {
        var body = IssueBodyComposer.ComposeCommentBody("The new detail.", "markus", "Acme HQ", [], []);

        Assert.Contains("_Also reported by **markus** in Discord server **Acme HQ**._", body);
        Assert.DoesNotContain("_Created by", body);
    }

    [Fact]
    public void Empty_additional_info_composes_to_just_the_attribution_line()
    {
        var body = IssueBodyComposer.ComposeCommentBody("", "markus", "Acme HQ", [], []);

        Assert.StartsWith(IssueBodyComposer.MetaMarker, body);
        Assert.EndsWith("_Also reported by **markus** in Discord server **Acme HQ**._", body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_server_the_bot_cannot_name_leaves_the_reporter_credited_alone(string guildName)
    {
        var issue = IssueBodyComposer.ComposeIssueBody("B", "markus", guildName, [], []);
        var comment = IssueBodyComposer.ComposeCommentBody("B", "markus", guildName, [], []);

        Assert.Contains("_Created by **markus** via Discord._", issue);
        Assert.Contains("_Also reported by **markus** via Discord._", comment);
        Assert.DoesNotContain("Discord server", issue);
        Assert.DoesNotContain("Discord server", comment);
    }

    [Fact]
    public void Images_render_as_markdown_gallery()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u", "g",
            [new UploadedImage("a.png", "https://x/a"), new UploadedImage("b.png", "https://x/b")], []);
        Assert.Contains("### Screenshots", body);
        Assert.Contains("![a.png](https://x/a)", body);
        Assert.Contains("![b.png](https://x/b)", body);
    }

    /// <summary>
    /// The code block is the bot's reading of the repository, so it sits behind the marker: two reports about
    /// different bugs in the same file must not look alike to the duplicate finder because of it.
    /// </summary>
    [Fact]
    public void Code_context_goes_behind_the_marker_and_before_the_footer()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u", "g", [], [], "### Relevant code\n- `src/a.cs`");

        var marker = body.IndexOf(IssueBodyComposer.MetaMarker, StringComparison.Ordinal);
        var code = body.IndexOf("### Relevant code", StringComparison.Ordinal);
        Assert.True(code > marker);
        Assert.True(code < body.IndexOf("_Created by", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_uploads_are_noted()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u", "g", [], ["x.png", "y.png"]);
        Assert.Contains("x.png, y.png", body);
        Assert.Contains("upload failed", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Image_names_cannot_break_out_of_their_markdown_link()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u", "g",
            [new UploadedImage("shot](http://evil)![x\n<b>`.png", "https://x/a")], []);

        Assert.Contains(
            @"![shot\]\(http://evil\)!\[x \<b>\`.png](https://x/a)", body);
        Assert.DoesNotContain("](http://evil)", body); // no second link to escape into
    }

    [Fact]
    public void Failed_upload_names_cannot_break_out_of_their_note()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u", "g", [], ["a](x).png", "b`.png"]);

        Assert.Contains(@"a\]\(x\).png, b\`.png", body);
    }

    [Fact]
    public void The_reporter_name_cannot_break_out_of_the_footer()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "ev[il](http://evil)\nx", "Acme HQ", [], []);

        Assert.Contains(
            @"_Created by **ev\[il\]\(http://evil\) x** in Discord server **Acme HQ**._", body);
    }

    [Fact]
    public void The_server_name_cannot_break_out_of_the_footer()
    {
        // A server name is no more trustworthy than a display name: whoever owns the guild picks it.
        var body = IssueBodyComposer.ComposeIssueBody(
            "B", "u", "Evil](http://evil)![x\\<b>\nrest", [], []);

        Assert.Contains(
            @"_Created by **u** in Discord server **Evil\]\(http://evil\)!\[x\\\<b> rest**._", body);
        Assert.DoesNotContain("](http://evil)", body); // no link to escape into
    }

    [Fact]
    public void A_backslash_cannot_re_arm_the_character_after_it()
    {
        // Left unescaped, an attacker's backslash lands in front of the one Escape prepends; the pair
        // renders as a single literal backslash and the "<" behind it is armed again — and GitHub renders
        // <a href> as a live link.
        var body = IssueBodyComposer.ComposeIssueBody(
            "B", """\<a href="https://evil.example">x\</a>""", "Acme HQ", [], []);

        Assert.Contains(
            """_Created by **\\\<a href="https://evil.example">x\\\</a>** in Discord server **Acme HQ**._""",
            body);
    }

    /// <summary>
    /// A late edit replaces the bot's part of a created issue and nothing else: text a maintainer changed
    /// above the marker in the meantime survives it.
    /// </summary>
    [Fact]
    public void Replacing_the_boilerplate_keeps_the_text_above_the_marker_as_it_now_reads()
    {
        var created = IssueBodyComposer.ComposeIssueBody("The body.", "markus", "Acme HQ", [], []);
        var editedSince = created.Replace("The body.", "The body, edited by a maintainer.");
        var withContext = IssueBodyComposer.ComposeIssueBody(
            "The body.", "markus", "Acme HQ", [], [], "### Relevant code\n- `src/a.cs`");

        var body = IssueBodyComposer.ReplaceBoilerplate(editedSince, withContext);

        Assert.NotNull(body);
        Assert.StartsWith("The body, edited by a maintainer.", body);
        Assert.Equal(1, CountMarkers(body));
        var marker = body.IndexOf(IssueBodyComposer.MetaMarker, StringComparison.Ordinal);
        Assert.Contains("### Relevant code", body[marker..]);
        Assert.EndsWith("_Created by **markus** in Discord server **Acme HQ**._", body);
    }

    /// <summary>Without its marker the body is no longer the bot's to rewrite, so nothing is spliced.</summary>
    [Fact]
    public void A_body_whose_marker_was_removed_is_not_replaced()
    {
        var withContext = IssueBodyComposer.ComposeIssueBody("B", "u", "g", [], [], "### Relevant code");

        Assert.Null(IssueBodyComposer.ReplaceBoilerplate("Rewritten from scratch by a maintainer.", withContext));
    }

    [Fact]
    public void Comment_body_carries_images_and_the_also_reported_footer()
    {
        var body = IssueBodyComposer.ComposeCommentBody("B", "u", "g",
            [new UploadedImage("a.png", "https://x/a")], []);
        Assert.Contains("![a.png](https://x/a)", body);
        Assert.Contains("_Also reported by **u** in Discord server **g**._", body);
    }
}
