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

    [Fact]
    public void Modal_plan_uses_the_only_app_without_a_dropdown()
    {
        var (app, choices, error) = AppResolution.PlanModal([App("mira")]);

        Assert.Null(error);
        Assert.Null(choices);
        Assert.Equal("mira", app!.Name);
    }

    [Fact]
    public void Modal_plan_offers_a_dropdown_when_several_apps_are_configured()
    {
        var (app, choices, error) = AppResolution.PlanModal([App("mira"), App("nova")]);

        Assert.Null(error);
        Assert.Null(app);
        Assert.Equal(["mira", "nova"], choices!.Select(a => a.Name));
    }

    [Fact]
    public void Modal_plan_reports_a_server_with_no_configured_app()
    {
        var (app, choices, error) = AppResolution.PlanModal([]);

        Assert.Null(app);
        Assert.Null(choices);
        Assert.Equal("No app is configured for this server.", error);
    }
}
