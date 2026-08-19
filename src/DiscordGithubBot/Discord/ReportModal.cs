using Discord;
using Discord.Interactions;

namespace DiscordGithubBot.Discord;

/// <summary>
/// The one form a reporter fills in: what happened, plus optional screenshots. Which app the report is
/// for is decided before the modal opens and travels in the modal's custom id, so the form itself stays
/// a single description field and a file picker.
/// </summary>
public class ReportModal : IModal
{
    public string Title => "Report";

    [InputLabel("What happened? Include steps if you can.")]
    [ModalTextInput("description", TextInputStyle.Paragraph, "Describe the issue or feature...", maxLength: 3000)]
    public string Description { get; set; } = "";

    [RequiredInput(false)]
    [InputLabel("Screenshots (optional)")]
    [ModalFileUpload("screenshots", minValues: 0, maxValues: 10)]
    public IAttachment[] Screenshots { get; set; } = [];
}
