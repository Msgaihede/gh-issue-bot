using DiscordGithubBot.Configuration;
using DiscordGithubBot.Discord;

namespace DiscordGithubBot.Tests.Discord;

public class AppResolutionTests
{
    private static AppConfig App(string name) => new() { Name = name, Repo = $"acme/{name}" };

    [Fact]
    public void Uses_the_only_app_when_no_name_is_given()
    {
        var (app, error) = AppResolution.Resolve([App("mira")], null);

        Assert.Null(error);
        Assert.Equal("mira", app!.Name);
    }

    [Fact]
    public void Asks_which_app_when_several_are_configured()
    {
        var (app, error) = AppResolution.Resolve([App("mira"), App("nova")], null);

        Assert.Null(app);
        Assert.Contains("mira, nova", error);
    }

    [Fact]
    public void Matches_a_named_app_ignoring_case_and_padding()
    {
        var (app, error) = AppResolution.Resolve([App("mira"), App("nova")], " NOVA ");

        Assert.Null(error);
        Assert.Equal("nova", app!.Name);
    }

    [Fact]
    public void Lists_the_valid_names_when_the_named_app_is_unknown()
    {
        var (app, error) = AppResolution.Resolve([App("mira"), App("nova")], "orion");

        Assert.Null(app);
        Assert.Contains("orion", error);
        Assert.Contains("mira, nova", error);
    }

    [Fact]
    public void Reports_a_server_with_no_configured_app()
    {
        var (app, error) = AppResolution.Resolve([], "mira");

        Assert.Null(app);
        Assert.Equal("No app is configured for this server.", error);
    }
}
