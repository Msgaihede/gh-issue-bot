using Discord;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Discord;

namespace DiscordGithubBot.Tests.Discord;

public class ReportModalTests
{
    [Fact]
    public void App_picker_lists_every_app_with_the_repo_as_its_value()
    {
        var label = ReportModal.BuildAppPicker([
            new AppConfig { Name = "mira", Repo = "acme/mira" },
            new AppConfig { Name = "nova", Repo = "acme/nova" }]);

        var menu = Assert.IsType<SelectMenuBuilder>(label.Component);
        Assert.Equal(ReportModal.AppSelectId, menu.CustomId);
        Assert.Equal(["mira", "nova"], menu.Options.Select(o => o.Label));
        Assert.Equal(["acme/mira", "acme/nova"], menu.Options.Select(o => o.Value));
    }

    [Fact]
    public void Pick_token_cannot_collide_with_a_repository()
    {
        // Repositories are validated to "owner/repo", so a token without a slash is unmistakable.
        Assert.DoesNotContain('/', ReportModal.PickAppToken);
    }
}
