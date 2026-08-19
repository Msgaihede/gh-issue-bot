using Discord;
using Discord.Interactions;
using DiscordGithubBot.Configuration;

namespace DiscordGithubBot.Discord;

/// <summary>
/// The one form a reporter fills in: what happened, plus optional screenshots. Which app the report is
/// for travels in the modal's custom id when the guild has one app; with several, the form opens with
/// an app dropdown on top and the custom id carries <see cref="PickAppToken"/> instead.
/// </summary>
public class ReportModal : IModal
{
    /// <summary>
    /// Stands in for the repository in the modal custom id when the reporter picks the app inside the
    /// modal. Cannot collide with a real repository: those are validated to the "owner/repo" shape,
    /// which always contains a slash.
    /// </summary>
    public const string PickAppToken = "-";

    /// <summary>Custom id of the app dropdown, when the modal carries one.</summary>
    public const string AppSelectId = "app";

    public string Title => "Report";

    [InputLabel("What happened? Include steps if you can.")]
    [ModalTextInput("description", TextInputStyle.Paragraph, "Describe the issue or feature...", maxLength: 3000)]
    public string Description { get; set; } = "";

    [RequiredInput(false)]
    [InputLabel("Screenshots (optional)")]
    [ModalFileUpload("screenshots", minValues: 0, maxValues: 10)]
    public IAttachment[] Screenshots { get; set; } = [];

    /// <summary>
    /// The "App" dropdown shown above the description when the guild maps to several apps. Built by
    /// hand rather than declared on the class: the options are the guild's configured apps, which an
    /// attribute cannot know, and the submit handler reads the choice from the raw modal data.
    /// </summary>
    public static LabelBuilder BuildAppPicker(IEnumerable<AppConfig> apps)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId(AppSelectId)
            .WithPlaceholder("Which app is this about?");

        foreach (var app in apps) menu.AddOption(app.Name, app.Repo);

        return new LabelBuilder().WithLabel("App").WithComponent(menu);
    }
}
