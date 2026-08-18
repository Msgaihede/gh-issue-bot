using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;

namespace DiscordGithubBot.Tests.Pipeline;

public class IssueBodyComposerTests
{
    [Fact]
    public void Minimal_body_has_reporter_footer_only()
    {
        var body = IssueBodyComposer.ComposeIssueBody("The body.", "markus", [], [], null);
        Assert.StartsWith("The body.", body);
        Assert.Contains("_Reported by **markus** via Discord._", body);
        Assert.DoesNotContain("Screenshots", body);
        Assert.DoesNotContain("regression", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("upload failed", body);
    }

    [Fact]
    public void Images_render_as_markdown_gallery()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u",
            [new UploadedImage("a.png", "https://x/a"), new UploadedImage("b.png", "https://x/b")], [], null);
        Assert.Contains("### Screenshots", body);
        Assert.Contains("![a.png](https://x/a)", body);
        Assert.Contains("![b.png](https://x/b)", body);
    }

    [Fact]
    public void Regression_reference_is_included()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u", [], [], 42);
        Assert.Contains("Possible regression of #42.", body);
    }

    [Fact]
    public void Failed_uploads_are_noted()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u", [], ["x.png", "y.png"], null);
        Assert.Contains("x.png, y.png", body);
        Assert.Contains("upload failed", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Image_names_cannot_break_out_of_their_markdown_link()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u",
            [new UploadedImage("shot](http://evil)![x\n<b>`.png", "https://x/a")], [], null);

        Assert.Contains(
            @"![shot\]\(http://evil\)!\[x \<b>\`.png](https://x/a)", body);
        Assert.DoesNotContain("](http://evil)", body); // no second link to escape into
    }

    [Fact]
    public void Failed_upload_names_cannot_break_out_of_their_note()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "u", [], ["a](x).png", "b`.png"], null);

        Assert.Contains(@"a\]\(x\).png, b\`.png", body);
    }

    [Fact]
    public void The_reporter_name_cannot_break_out_of_the_footer()
    {
        var body = IssueBodyComposer.ComposeIssueBody("B", "ev[il](http://evil)\nx", [], [], null);

        Assert.Contains(@"_Reported by **ev\[il\]\(http://evil\) x** via Discord._", body);
    }

    [Fact]
    public void Comment_body_never_has_regression_line()
    {
        var body = IssueBodyComposer.ComposeCommentBody("B", "u",
            [new UploadedImage("a.png", "https://x/a")], []);
        Assert.Contains("![a.png](https://x/a)", body);
        Assert.Contains("_Reported by **u** via Discord._", body);
        Assert.DoesNotContain("regression", body, StringComparison.OrdinalIgnoreCase);
    }
}
