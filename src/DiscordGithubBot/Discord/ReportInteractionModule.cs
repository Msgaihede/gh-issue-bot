using System.Text.Json;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordGithubBot.Ai;
using DiscordGithubBot.Configuration;
using DiscordGithubBot.Data;
using DiscordGithubBot.GitHub;
using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Discord;

/// <summary>
/// Every slash command, modal submit and button click the bot answers. The module itself only routes and
/// answers: the decisions live in <see cref="IReportPipeline"/>, the wording in <see cref="OutcomeRenderer"/>
/// and the app lookup in <see cref="AppResolution"/>.
/// </summary>
/// <remarks>
/// Every handler is declared <see cref="RunMode.Sync"/>: <c>BotService</c> owns both the background task the
/// interaction runs on and the DI scope it resolves from, and it can only dispose that scope once the
/// handler has finished — which requires the framework to run the handler inline rather than detaching it.
/// </remarks>
public class ReportInteractionModule(
    BotOptions options,
    IReportPipeline pipeline,
    AttachmentDownloader downloader,
    IGitHubService gitHub,
    DiscordSocketClient client,
    ILogger<ReportInteractionModule> logger)
    : InteractionModuleBase<SocketInteractionContext>
{
    private const string AppOptionDescription = "Which app (only needed when several are configured)";
    // One wording for all three ways a pending report stops being actionable — expired, unknown, or
    // claimed by a click that is already talking to GitHub. A reporter cannot tell them apart and does
    // not need to: in every case the answer is to start again.
    private const string ExpiredMessage =
        "That report is no longer waiting — it expired, or another click is already handling it. " +
        "Please run the command again.";
    private const string CancelledMessage = "Cancelled — nothing was created.";
    private const string GuildOnlyMessage = "This command only works inside a server.";
    private const string GenericErrorMessage =
        "Something went wrong while processing your report. Please try again later.";
    private const string NormalizationErrorMessage =
        "Sorry — I couldn't process that report right now. Please try again.";
    private const string RenderErrorMessage =
        "I read your report but couldn't show you the result. Please run the command again.";

    // --- slash commands ---

    [SlashCommand("report-issue", "Report a bug in the app", runMode: RunMode.Sync)]
    public Task ReportIssue() => OpenModalAsync(ReportType.Bug);

    [SlashCommand("request-feature", "Request a new feature", runMode: RunMode.Sync)]
    public Task RequestFeature() => OpenModalAsync(ReportType.Feature);

    [SlashCommand("issues", "List open GitHub issues", runMode: RunMode.Sync)]
    public async Task Issues([Summary(description: AppOptionDescription)] string? app = null)
    {
        var (resolved, error) = ResolveApp(app);
        if (error is not null)
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);

        try
        {
            var issues = await gitHub.ListIssuesAsync(resolved!, "open", null);
            await FollowupAsync(components: OutcomeRenderer.RenderIssueList(resolved!.Name, issues), ephemeral: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Listing issues for {Repo} failed.", resolved!.Repo);
            await FollowupAsync("I couldn't reach GitHub just now. Please try again later.", ephemeral: true);
        }
    }

    // --- modal submit ---

    [ModalInteraction("report-modal|*|*", runMode: RunMode.Sync)]
    public async Task OnReportModal(string typeToken, string repoToken, ReportModal modal)
    {
        // The three-second acknowledgement deadline comes before everything else, including the download.
        await DeferAsync(ephemeral: true);

        var (app, pickedRepo) = ResolveModalApp(repoToken);
        if (app is null)
        {
            logger.LogWarning("Modal submitted for unknown or out-of-guild repository {Repo}.", pickedRepo);
            await FollowupAsync(
                pickedRepo.Length == 0
                    ? "I couldn't tell which app you picked. Please run the command again."
                    : "That app is no longer configured. Please run the command again.",
                ephemeral: true);
            return;
        }

        var type = typeToken == "bug" ? ReportType.Bug : ReportType.Feature;

        // Downloaded before the slow work: Discord's attachment URLs expire, the reporter's bytes must not.
        var (payloads, skipped) = await downloader.DownloadAsync(modal.Screenshots ?? []);
        var notice = skipped.Count == 0
            ? null
            : $"⚠️ Skipped (not an image / too large / failed): {string.Join(", ", skipped)}";

        ReportOutcome outcome;
        try
        {
            // The slash command that opens this modal refuses to run outside a server (OpenModalAsync),
            // so Guild is set on every path that reaches here; the null-conditional is belt and braces,
            // and an empty name simply drops the server half of the GitHub footer.
            outcome = await pipeline.ProcessAsync(new ReportSubmission(
                app, type, Context.User.Id, Context.User.GlobalName ?? Context.User.Username,
                Context.Guild?.Name ?? "", modal.Description, payloads));
        }
        catch (NormalizationException ex)
        {
            logger.LogWarning(ex, "Normalization failed for a report in {Repo}.", app.Repo);
            await FollowupAsync(NormalizationErrorMessage, ephemeral: true);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Report pipeline failed for {Repo}.", app.Repo);
            await FollowupAsync(GenericErrorMessage, ephemeral: true);
            return;
        }

        // Rendering and delivery sit outside the pipeline's catch on purpose: a payload Discord refuses
        // is not a failed report, and logging it as one sends whoever reads the log after the wrong bug.
        // The draft is saved either way, so the fallback reply says what actually happened.
        try
        {
            await FollowupAsync(components: OutcomeRenderer.Render(outcome, notice), ephemeral: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not deliver the report outcome for {Repo}.", app.Repo);

            try
            {
                await FollowupAsync(RenderErrorMessage, ephemeral: true);
            }
            catch (Exception fallbackEx)
            {
                logger.LogWarning(fallbackEx, "Could not deliver the fallback reply for {Repo} either.", app.Repo);
            }
        }
    }

    // --- component handlers ---
    // The wildcard captures are declared because Discord.Net matches on them, but the custom id is
    // re-read through CustomIds.TryParse so that one validated codec decides what a click means.

    [ComponentInteraction("rep|create|*|*", runMode: RunMode.Sync)]
    public Task OnCreate(string pendingSegment, string issueSegment) => RunAsync(async (id, regressionOf) =>
    {
        // Peeked before the create call: creating deletes the pending report, and the announcement needs
        // to know which app (and which reporter) it belongs to.
        var pending = await pipeline.PeekAsync(id);
        if (pending is null)
        {
            await FollowupEphemeralAsync(ExpiredMessage);
            return;
        }

        var issue = await pipeline.CreateIssueAsync(id, regressionOf == 0 ? null : regressionOf);

        var app = options.AppByRepo(pending.RepoKey);
        if (app is null) logger.LogWarning("No app configured for {Repo}; skipping the announcement.", pending.RepoKey);
        else await AnnounceAsync(app, issue, pending.Type, pending.ReporterDisplayName);

        await FollowupEphemeralAsync(OutcomeRenderer.RenderCreated(issue));
    });

    [ComponentInteraction("rep|cancel|*|*", runMode: RunMode.Sync)]
    public Task OnCancel(string pendingSegment, string issueSegment) => RunAsync(async (id, _) =>
    {
        await pipeline.CancelAsync(id);
        await FollowupEphemeralAsync(CancelledMessage);
    });

    [ComponentInteraction("rep|comment|*|*", runMode: RunMode.Sync)]
    public Task OnComment(string pendingSegment, string issueSegment) => RunAsync(async (id, issueNumber) =>
    {
        var comment = await pipeline.AddCommentAsync(id, issueNumber);
        await FollowupEphemeralAsync(OutcomeRenderer.RenderCommented(comment));
    });

    [ComponentInteraction("rep|draft|*|*", runMode: RunMode.Sync)]
    public Task OnDraft(string pendingSegment, string issueSegment) =>
        RunAsync((id, _) => ShowDraftAsync(id, regressionOf: 0, heading: null));

    [ComponentInteraction("rep|stillopen|*|*", runMode: RunMode.Sync)]
    public Task OnStillOpen(string pendingSegment, string issueSegment) =>
        RunAsync((id, issueNumber) => ShowDraftAsync(
            id, issueNumber, $"**Filing a new issue that references #{issueNumber}:**"));

    [ComponentInteraction("rep|fixed|*|*", runMode: RunMode.Sync)]
    public Task OnFixed(string pendingSegment, string issueSegment) => RunAsync(async (id, issueNumber) =>
    {
        // The repository is read before cancelling, because cancelling drops the row that holds it.
        var pending = await pipeline.PeekAsync(id);
        await pipeline.CancelAsync(id);

        if (pending is null) await FollowupEphemeralAsync(CancelledMessage);
        else await FollowupEphemeralAsync(OutcomeRenderer.RenderFixed(pending.RepoKey, issueNumber));
    });

    [ComponentInteraction("rep|pick|*|*", runMode: RunMode.Sync)]
    public Task OnPick(string pendingSegment, string issueSegment, string[] selections) => RunAsync(async (id, _) =>
    {
        var pending = await pipeline.PeekAsync(id);
        if (pending is null)
        {
            await FollowupEphemeralAsync(ExpiredMessage);
            return;
        }

        var picked = selections.Length > 0 && int.TryParse(selections[0], out var number) ? number : 0;
        var candidates = JsonSerializer.Deserialize<List<CandidateIssue>>(pending.CandidatesJson) ?? [];
        var candidate = candidates.FirstOrDefault(c => c.Number == picked);

        if (candidate is null)
        {
            logger.LogWarning("Picked issue #{Number} is not a candidate of pending report {PendingId}.", picked, id);
            await ShowDraftAsync(id, regressionOf: 0, heading: "**I couldn't find that issue — here's your draft:**");
            return;
        }

        await FollowupEphemeralAsync(OutcomeRenderer.RenderMatch(candidate, id));
    });

    // --- shared flow ---

    private async Task OpenModalAsync(ReportType type)
    {
        if (Context.Guild is null)
        {
            await RespondAsync(GuildOnlyMessage, ephemeral: true);
            return;
        }

        var (app, choices, error) = AppResolution.PlanModal(options.AppsForGuild(Context.Guild.Id));
        if (error is not null)
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        // The chosen repository rides along in the modal's custom id, so the submit handler needs no
        // state. With several apps the choice hasn't been made yet: a placeholder token rides instead,
        // and a dropdown of the guild's apps goes on top of the form.
        var typeToken = type == ReportType.Bug ? "bug" : "feature";
        if (app is not null)
        {
            await RespondWithModalAsync<ReportModal>($"report-modal|{typeToken}|{app.Repo}");
            return;
        }

        await RespondWithModalAsync<ReportModal>(
            $"report-modal|{typeToken}|{ReportModal.PickAppToken}",
            modifyModal: modal => modal.Components.Insert(0, ReportModal.BuildAppPicker(choices!)));
    }

    private (AppConfig? App, string? Error) ResolveApp(string? appName) =>
        Context.Guild is null
            ? (null, GuildOnlyMessage)
            : AppResolution.Resolve(options.AppsForGuild(Context.Guild.Id), appName);

    /// <summary>
    /// The app a submitted modal is for: named by the custom id when the guild had one app, read from
    /// the app dropdown when the reporter picked one inside the modal. Resolved against the guild's
    /// own apps, not every configured one — both values echo back through the client, and another
    /// guild's repository is not a valid pick here.
    /// </summary>
    private (AppConfig? App, string Repo) ResolveModalApp(string repoToken)
    {
        var selectValue = repoToken == ReportModal.PickAppToken
            ? ((IModalInteraction)Context.Interaction).Data.Components
                .FirstOrDefault(c => c.CustomId == ReportModal.AppSelectId)?
                .Values?.FirstOrDefault()
            : null;

        var repo = AppResolution.PickedRepo(repoToken, selectValue);
        var app = Context.Guild is null
            ? null
            : options.AppsForGuild(Context.Guild.Id)
                .FirstOrDefault(a => string.Equals(a.Repo, repo, StringComparison.OrdinalIgnoreCase));

        return (app, repo);
    }

    /// <summary>
    /// Acknowledges the click, re-reads the custom id and runs the action, turning the two expected
    /// failures into plain language and anything else into a logged, generic apology. An interaction is
    /// never left unanswered.
    /// </summary>
    private async Task RunAsync(Func<Guid, int, Task> action)
    {
        await AcknowledgeAsync();

        var customId = ((IComponentInteraction)Context.Interaction).Data.CustomId;
        if (!CustomIds.TryParse(customId, out _, out var id, out var issueNumber))
        {
            logger.LogWarning("Ignoring a component interaction with an unreadable custom id {CustomId}.", customId);
            await FollowupEphemeralAsync("Sorry — I couldn't read that button. Please run the command again.");
            return;
        }

        try
        {
            await action(id, issueNumber);
        }
        catch (ExpiredPendingReportException)
        {
            await FollowupEphemeralAsync(ExpiredMessage);
        }
        catch (NormalizationException ex)
        {
            logger.LogWarning(ex, "Normalization failed while handling {CustomId}.", customId);
            await FollowupEphemeralAsync(NormalizationErrorMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Component interaction {CustomId} failed.", customId);
            await FollowupEphemeralAsync(GenericErrorMessage);
        }
    }

    /// <summary>
    /// Answers within the three-second window by replacing the clicked message with a "working" note,
    /// which also takes its buttons away — a second click on the same message becomes impossible instead
    /// of racing the first. If Discord refuses the update, a plain defer still acknowledges the click.
    /// </summary>
    private async Task AcknowledgeAsync()
    {
        try
        {
            await ((IComponentInteraction)Context.Interaction).UpdateAsync(message =>
            {
                message.Components = OutcomeRenderer.RenderWorking();
                message.Flags = MessageFlags.ComponentsV2;
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not replace the clicked message; falling back to a plain defer.");
            if (!Context.Interaction.HasResponded) await DeferAsync(ephemeral: true);
        }
    }

    private async Task ShowDraftAsync(Guid id, int regressionOf, string? heading)
    {
        var pending = await pipeline.PeekAsync(id);
        if (pending is null)
        {
            await FollowupEphemeralAsync(ExpiredMessage);
            return;
        }

        await FollowupEphemeralAsync(OutcomeRenderer.RenderDraftPreview(
            new IssueDraft(pending.DraftTitle, pending.DraftBody), id, regressionOf, heading));
    }

    /// <summary>Posts the public announcement in every channel the app is configured for; never throws.</summary>
    private async Task AnnounceAsync(AppConfig app, CreatedIssueResult issue, ReportType type, string reporter)
    {
        foreach (var channelId in app.ChannelIds)
        {
            try
            {
                // The gateway cache first, then a REST lookup: a channel the bot has not seen yet is
                // still a configured channel, and "unknown" should mean unknown rather than uncached.
                var channel = client.GetChannel(channelId) as IMessageChannel
                    ?? await ((IDiscordClient)client).GetChannelAsync(channelId) as IMessageChannel;

                if (channel is null)
                {
                    logger.LogWarning(
                        "Channel {ChannelId} configured for {App} is unknown or cannot take messages.",
                        channelId, app.Name);
                    continue;
                }

                await channel.SendMessageAsync(
                    components: OutcomeRenderer.RenderAnnouncement(issue, app.Name, reporter, type),
                    flags: MessageFlags.ComponentsV2);
            }
            catch (Exception ex)
            {
                // An announcement is a nicety; the issue already exists and the reporter still gets told.
                logger.LogWarning(
                    ex, "Failed to announce issue #{Number} in channel {ChannelId}.", issue.Number, channelId);
            }
        }
    }

    private Task FollowupEphemeralAsync(string text) => FollowupAsync(text, ephemeral: true);

    private Task FollowupEphemeralAsync(MessageComponent components) => FollowupAsync(components: components, ephemeral: true);
}
