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
    private const string ExpiredMessage = "This report session has expired — please run the command again.";
    private const string CancelledMessage = "Cancelled — nothing was created.";
    private const string GenericErrorMessage =
        "Something went wrong while processing your report. Please try again later.";
    private const string NormalizationErrorMessage =
        "Sorry — I couldn't process that report right now. Please try again.";
    private const string RenderErrorMessage =
        "I read your report but couldn't show you the result. Please run the command again.";

    // --- slash commands ---

    [SlashCommand("report-issue", "Report a bug in the app", runMode: RunMode.Sync)]
    public Task ReportIssue([Summary(description: AppOptionDescription)] string? app = null) =>
        OpenModalAsync(ReportType.Bug, app);

    [SlashCommand("request-feature", "Request a new feature", runMode: RunMode.Sync)]
    public Task RequestFeature([Summary(description: AppOptionDescription)] string? app = null) =>
        OpenModalAsync(ReportType.Feature, app);

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
    public async Task OnReportModal(string typeToken, string repo, ReportModal modal)
    {
        // The three-second acknowledgement deadline comes before everything else, including the download.
        await DeferAsync(ephemeral: true);

        var app = options.AppByRepo(repo);
        if (app is null)
        {
            logger.LogWarning("Modal submitted for unknown repository {Repo}.", repo);
            await FollowupAsync("That app is no longer configured. Please run the command again.", ephemeral: true);
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
            outcome = await pipeline.ProcessAsync(new ReportSubmission(
                app, type, Context.User.Id, Context.User.GlobalName ?? Context.User.Username,
                modal.Description, payloads));
        }
        catch (NormalizationException ex)
        {
            logger.LogWarning(ex, "Normalization failed for a report in {Repo}.", repo);
            await FollowupAsync(NormalizationErrorMessage, ephemeral: true);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Report pipeline failed for {Repo}.", repo);
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
            logger.LogError(ex, "Could not deliver the report outcome for {Repo}.", repo);

            try
            {
                await FollowupAsync(RenderErrorMessage, ephemeral: true);
            }
            catch (Exception fallbackEx)
            {
                logger.LogWarning(fallbackEx, "Could not deliver the fallback reply for {Repo} either.", repo);
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

    private async Task OpenModalAsync(ReportType type, string? appName)
    {
        var (app, error) = ResolveApp(appName);
        if (error is not null)
        {
            await RespondAsync(error, ephemeral: true);
            return;
        }

        // The chosen repository rides along in the modal's custom id, so the submit handler needs no state.
        var typeToken = type == ReportType.Bug ? "bug" : "feature";
        await RespondWithModalAsync<ReportModal>($"report-modal|{typeToken}|{app!.Repo}");
    }

    private (AppConfig? App, string? Error) ResolveApp(string? appName) =>
        Context.Guild is null
            ? (null, "This command only works inside a server.")
            : AppResolution.Resolve(options.AppsForGuild(Context.Guild.Id), appName);

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
