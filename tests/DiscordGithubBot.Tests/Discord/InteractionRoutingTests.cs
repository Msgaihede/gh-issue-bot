using Discord.Interactions;
using Discord.WebSocket;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Discord;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiscordGithubBot.Tests.Discord;

/// <summary>
/// Builds the interaction module exactly as the bot does at startup. Discord.Net validates command names,
/// wildcard arity and the modal definition while building, so this is where a mistyped route or a modal
/// Discord would reject shows up — the module's own handlers are exercised by hand in the release check.
/// </summary>
public class InteractionRoutingTests
{
    private static async Task<ModuleInfo> BuildModuleAsync()
    {
        using var client = new DiscordSocketClient();
        using var interactions = new InteractionService(client, BotService.CreateConfig());

        var services = new ServiceCollection()
            .AddSingleton(new BotOptions())
            .AddSingleton(Substitute.For<IReportPipeline>())
            .AddSingleton(Substitute.For<IGitHubService>())
            .AddSingleton(new AttachmentDownloader(new HttpClient(), NullLogger<AttachmentDownloader>.Instance))
            .AddSingleton(client)
            .AddSingleton(NullLoggerFactory.Instance)
            .AddLogging()
            .BuildServiceProvider();

        return await interactions.AddModuleAsync<ReportInteractionModule>(services);
    }

    [Fact]
    public async Task Registers_the_three_slash_commands()
    {
        var module = await BuildModuleAsync();

        Assert.Equal(
            ["issues", "report-issue", "request-feature"],
            module.SlashCommands.Select(c => c.Name).Order());
    }

    [Fact]
    public async Task Routes_every_custom_id_action_to_a_handler()
    {
        var module = await BuildModuleAsync();

        string[] actions =
        [
            CustomIds.Create, CustomIds.Cancel, CustomIds.Comment, CustomIds.Draft,
            CustomIds.StillOpen, CustomIds.Fixed, CustomIds.Pick,
        ];

        Assert.Equal(
            actions.Select(a => $"{CustomIds.Prefix}|{a}|*|*").Order(),
            module.ComponentCommands.Select(c => c.Name).Order());
    }

    [Fact]
    public async Task Registers_the_report_modal()
    {
        var module = await BuildModuleAsync();

        var modal = Assert.Single(module.ModalCommands);
        Assert.Equal("report-modal|*|*", modal.Name);
    }
}
