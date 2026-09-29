using Discord;
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
            .AddSingleton(new ReportRateLimiter(new BotOptions(), TimeProvider.System))
            .AddSingleton(new AttachmentDownloader(new HttpClient(), NullLogger<AttachmentDownloader>.Instance))
            .AddSingleton(client)
            .AddSingleton(NullLoggerFactory.Instance)
            .AddLogging()
            .BuildServiceProvider();

        return await interactions.AddModuleAsync<ReportInteractionModule>(services);
    }

    /// <summary>One reporting command; the decision model, not the reporter, decides bug or feature.</summary>
    [Fact]
    public async Task Registers_the_report_command_the_install_link_and_the_issue_list()
    {
        var module = await BuildModuleAsync();

        Assert.Equal(["issue", "issue-install", "issues"], module.SlashCommands.Select(c => c.Name).Order());
    }

    /// <summary>
    /// The user-install requirement lives in this metadata: without the user integration type the install
    /// link adds nothing, and without the DM and private-channel contexts a user install could only be used
    /// in servers anyway. Declared once on the module; this pins that it reaches every command.
    /// </summary>
    [Fact]
    public async Task Every_command_is_user_installable_and_usable_in_servers_dms_and_group_dms()
    {
        var module = await BuildModuleAsync();

        Assert.All(module.SlashCommands, command =>
        {
            Assert.Equal(
                [ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall],
                command.IntegrationTypes.Order());
            Assert.Equal(
                [InteractionContextType.Guild, InteractionContextType.BotDm, InteractionContextType.PrivateChannel],
                command.ContextTypes!.Order());
        });
    }

    [Fact]
    public async Task The_install_command_takes_no_options()
    {
        var module = await BuildModuleAsync();

        Assert.Empty(module.SlashCommands.Single(c => c.Name == "issue-install").Parameters);
    }

    [Fact]
    public async Task The_report_command_takes_no_options()
    {
        var module = await BuildModuleAsync();

        Assert.Empty(module.SlashCommands.Single(c => c.Name == "issue").Parameters);
    }

    [Fact]
    public async Task Routes_every_custom_id_action_to_a_handler()
    {
        var module = await BuildModuleAsync();

        string[] actions =
        [
            CustomIds.Create, CustomIds.Cancel, CustomIds.Comment, CustomIds.Draft, CustomIds.Pick,
        ];

        Assert.Equal(
            actions.Select(a => $"{CustomIds.Prefix}|{a}|*|*").Order(),
            module.ComponentCommands.Select(c => c.Name).Order());
    }

    [Fact]
    public async Task Issues_keeps_its_optional_app_option()
    {
        var module = await BuildModuleAsync();

        var issues = module.SlashCommands.Single(c => c.Name == "issues");
        var parameter = Assert.Single(issues.Parameters);
        Assert.Equal("app", parameter.Name);
        Assert.False(parameter.IsRequired);
    }

    [Fact]
    public async Task Registers_the_report_modal()
    {
        var module = await BuildModuleAsync();

        var modal = Assert.Single(module.ModalCommands);
        Assert.Equal("report-modal|*", modal.Name);
    }

    /// <summary>
    /// Builds the exact payload the multi-app path sends — the typed modal plus the app dropdown
    /// inserted by <c>modifyModal</c> — the same way <c>RespondWithModalAsync</c> does. The null
    /// interaction is safe: building the modal never touches it.
    /// </summary>
    [Fact]
    public async Task Multi_app_modal_carries_a_required_app_dropdown_on_top()
    {
        var module = await BuildModuleAsync();
        var modalInfo = Assert.Single(module.ModalCommands).Modal;

        var modal = await ((IDiscordInteraction)null!).ToModalAsync(
            $"report-modal|{ReportModal.PickAppToken}", modalInfo, (ReportModal)null!, null,
            builder => builder.Components.Insert(0, ReportModal.BuildAppPicker([
                new AppConfig { Name = "mira", Repo = "acme/mira" },
                new AppConfig { Name = "nova", Repo = "acme/nova" }])));

        var components = modal.Component.Components.ToList();
        Assert.Equal(3, components.Count);

        var label = Assert.IsType<LabelComponent>(components[0]);
        var menu = Assert.IsType<SelectMenuComponent>(label.Component);
        Assert.Equal(ReportModal.AppSelectId, menu.CustomId);
        Assert.True(menu.IsRequired);
        Assert.Equal(1, menu.MinValues);
        Assert.Equal(["acme/mira", "acme/nova"], menu.Options.Select(o => o.Value));
    }
}
