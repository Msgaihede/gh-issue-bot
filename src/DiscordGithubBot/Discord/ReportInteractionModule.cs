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
/// answers: the decisions live in <see cref="IReportPipeline"/>, the wording in <see cref="OutcomeRenderer"/>,
/// which apps the user may use in <see cref="AppAccess"/> and the pick among them in <see cref="AppResolution"/>.
/// </summary>
/// <remarks>
/// Every handler is declared <see cref="RunMode.Sync"/>: <c>BotService</c> owns both the background task the
/// interaction runs on and the DI scope it resolves from, and it can only dispose that scope once the
/// handler has finished — which requires the framework to run the handler inline rather than detaching it.
/// <para>
/// Every command can be installed on a server <em>or</em> on a user's own account, and runs in servers,
/// DMs with the bot and group DMs. In a user-installed context the bot is often not a member of the
/// server the command came from, so nothing here may rely on <c>Context.Guild</c> being set: the server a
/// command came from is read from the interaction's <c>GuildId</c>, and its name may be unknown.
/// </para>
/// </remarks>
[IntegrationType(ApplicationIntegrationType.GuildInstall, ApplicationIntegrationType.UserInstall)]
[CommandContextType(InteractionContextType.Guild, InteractionContextType.BotDm, InteractionContextType.PrivateChannel)]
public class ReportInteractionModule(
    BotOptions options,
    AppAccess appAccess,
    IReportPipeline pipeline,
    AttachmentDownloader downloader,
    IGitHubService gitHub,
    ReportRateLimiter rateLimiter,
    DiscordSocketClient client,
    ILogger<ReportInteractionModule> logger)
    : InteractionModuleBase<SocketInteractionContext>
{
    private const string AppOptionDescription = "Which app (only needed when several are available to you)";
    // One wording for all three ways a pending report stops being actionable — expired, unknown, or
    // claimed by a click that is already talking to GitHub. A reporter cannot tell them apart and does
    // not need to: in every case the answer is to start again.
    private const string ExpiredMessage =
        "That report is no longer waiting — it expired, or another click is already handling it. " +
        "Please run the command again.";
    private const string CancelledMessage = "Cancelled — nothing was created.";
    private const string GenericErrorMessage =
        "Something went wrong while processing your report. Please try again later.";
    private const string NormalizationErrorMessage =
        "Sorry — I couldn't process that report right now. Please try again.";
    private const string RenderErrorMessage =
        "I read your report but couldn't show you the result. Please run the command again.";

    // --- slash commands ---

    /// <summary>
    /// The one reporting command. Whether the report is a bug or a feature request is no longer the
    /// reporter's call: the decision model reads the report and decides (see <c>ReportClassifier</c>).
    /// </summary>
    [SlashCommand("issue", "Report a bug or request a feature", runMode: RunMode.Sync)]
    public Task Issue() => OpenModalAsync();

    /// <summary>
    /// Hands out the link that adds the bot to the caller's own Discord account, after which <c>/issue</c>
    /// works in any server, DM or group DM — not only in the servers the bot was added to.
    /// </summary>
    [SlashCommand("issue-install", "Get a link to add this bot to your own Discord account", runMode: RunMode.Sync)]
    public Task IssueInstall() =>
        RespondAsync(
            components: OutcomeRenderer.RenderInstallLink(InstallLinks.UserInstall(Context.Interaction.ApplicationId)),
            ephemeral: true);

    [SlashCommand("list-issues", "List open GitHub issues", runMode: RunMode.Sync)]
    public async Task ListIssues([Summary(description: AppOptionDescription)] string? app = null)
    {
        var (resolved, error) = await ResolveAppAsync(app);
        if (error is not null)
        {
            logger.LogInformation("Turned {User} ({UserId}) away: {Reason}", Context.User.Username, Context.User.Id, error);
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

    [ModalInteraction("report-modal|*", runMode: RunMode.Sync)]
    public async Task OnReportModal(string repoToken, ReportModal modal)
    {
        // The three-second acknowledgement deadline comes before everything else, including the download.
        await DeferAsync(ephemeral: true);

        // Checked again, not trusted from when the modal opened: the pick echoes back through the client,
        // and the reporter may have left the app's server in the meantime.
        var access = await AppsHereAsync();
        var (app, pickedRepo) = ResolveModalApp(repoToken, access.Apps);
        if (app is null)
        {
            logger.LogWarning("Modal submitted for an unknown or unavailable repository {Repo}.", pickedRepo);
            await FollowupAsync(
                access.Error ?? (pickedRepo.Length == 0
                    ? "I couldn't tell which app you picked. Please run the command again."
                    : "That app isn't available to you here any more. Please run the command again."),
                ephemeral: true);
            return;
        }

        // Checked again here, not only when the modal opened: one person can open several modals at once.
        // Counted from here on, because this is where the report starts to cost money.
        if (rateLimiter.RetryAfter(Context.User.Id) is { } retryAt)
        {
            LogRateLimited(retryAt);
            await FollowupAsync(RateLimitedMessage(retryAt), ephemeral: true);
            return;
        }

        rateLimiter.Record(Context.User.Id);

        // Downloaded before the slow work: Discord's attachment URLs expire, the reporter's bytes must not.
        var (payloads, skipped) = await downloader.DownloadAsync(modal.Screenshots ?? []);
        var notice = skipped.Count == 0
            ? null
            : $"⚠️ Skipped (not an image / too large / failed): {string.Join(", ", skipped)}";

        ReportOutcome outcome;
        try
        {
            // Guild is null in a DM — and in a server the bot is not a member of, which a user install
            // reaches. An empty name simply drops the server half of the GitHub footer.
            outcome = await pipeline.ProcessAsync(new ReportSubmission(
                app, Context.User.Id, Context.User.GlobalName ?? Context.User.Username,
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
    public Task OnCreate(string pendingSegment, string issueSegment) => RunAsync(async (id, _) =>
    {
        // Peeked before the create call: creating deletes the pending report, and the announcement needs
        // to know which app (and which reporter) it belongs to.
        var pending = await pipeline.PeekAsync(id);
        if (pending is null)
        {
            await AnswerClickAsync(ExpiredMessage);
            return;
        }

        var issue = await pipeline.CreateIssueAsync(id);

        // The reporter is answered first — the announcement is for everyone else and need not hold them up —
        // and the issue is announced even when answering fails, since it exists either way.
        try
        {
            await AnswerClickAsync(OutcomeRenderer.RenderCreated(issue));
        }
        finally
        {
            var app = options.AppByRepo(pending.RepoKey);
            if (app is null) logger.LogWarning("No app configured for {Repo}; skipping the announcement.", pending.RepoKey);
            else await AnnounceAsync(app, issue, pending.Type, pending.ReporterDisplayName);
        }
    });

    [ComponentInteraction("rep|cancel|*|*", runMode: RunMode.Sync)]
    public Task OnCancel(string pendingSegment, string issueSegment) => RunAsync(async (id, _) =>
    {
        await pipeline.CancelAsync(id);
        await AnswerClickAsync(CancelledMessage);
    });

    [ComponentInteraction("rep|comment|*|*", runMode: RunMode.Sync)]
    public Task OnComment(string pendingSegment, string issueSegment) => RunAsync(async (id, issueNumber) =>
    {
        var comment = await pipeline.AddCommentAsync(id, issueNumber);
        await AnswerClickAsync(OutcomeRenderer.RenderCommented(comment));
    });

    [ComponentInteraction("rep|draft|*|*", runMode: RunMode.Sync)]
    public Task OnDraft(string pendingSegment, string issueSegment) =>
        RunAsync((id, _) => ShowDraftAsync(id, heading: null));

    [ComponentInteraction("rep|pick|*|*", runMode: RunMode.Sync)]
    public Task OnPick(string pendingSegment, string issueSegment, string[] selections) => RunAsync(async (id, _) =>
    {
        var pending = await pipeline.PeekAsync(id);
        if (pending is null)
        {
            await AnswerClickAsync(ExpiredMessage);
            return;
        }

        var picked = selections.Length > 0 && int.TryParse(selections[0], out var number) ? number : 0;
        var candidates = JsonSerializer.Deserialize<List<CandidateIssue>>(pending.CandidatesJson) ?? [];
        var candidate = candidates.FirstOrDefault(c => c.Number == picked);

        if (candidate is null)
        {
            logger.LogWarning("Picked issue #{Number} is not a candidate of pending report {PendingId}.", picked, id);
            await ShowDraftAsync(id, heading: "**I couldn't find that issue — here's your draft:**");
            return;
        }

        await AnswerClickAsync(OutcomeRenderer.RenderMatch(candidate, id));
    });

    // --- shared flow ---

    private async Task OpenModalAsync()
    {
        // Refused before the reporter types anything; the submit handler checks again.
        if (rateLimiter.RetryAfter(Context.User.Id) is { } retryAt)
        {
            LogRateLimited(retryAt);
            await RespondAsync(RateLimitedMessage(retryAt), ephemeral: true);
            return;
        }

        var access = await AppsHereAsync();
        var (app, choices, error) = access.Error is null
            ? AppResolution.PlanModal(access.Apps)
            : (null, null, access.Error);
        if (error is not null)
        {
            logger.LogInformation("Turned {User} ({UserId}) away: {Reason}", Context.User.Username, Context.User.Id, error);
            await RespondAsync(error, ephemeral: true);
            return;
        }

        // The chosen repository rides along in the modal's custom id, so the submit handler needs no
        // state. With several apps the choice hasn't been made yet: a placeholder token rides instead,
        // and a dropdown of the guild's apps goes on top of the form.
        if (app is not null)
        {
            await RespondWithModalAsync<ReportModal>($"report-modal|{app.Repo}");
            return;
        }

        await RespondWithModalAsync<ReportModal>(
            $"report-modal|{ReportModal.PickAppToken}",
            modifyModal: modal => modal.Components.Insert(0, ReportModal.BuildAppPicker(choices!)));
    }

    private Task<AppAccessResult> AppsHereAsync() => appAccess.ForAsync(Context.Interaction.GuildId, Context.User.Id);

    private async Task<(AppConfig? App, string? Error)> ResolveAppAsync(string? appName)
    {
        var access = await AppsHereAsync();
        return access.Error is null ? AppResolution.Resolve(access.Apps, appName) : (null, access.Error);
    }

    private void LogRateLimited(DateTimeOffset retryAt) =>
        logger.LogInformation("{User} ({UserId}) is at the daily report limit; refused until {RetryAt:u}.",
            Context.User.Username, Context.User.Id, retryAt);

    private static string RateLimitedMessage(DateTimeOffset retryAt) =>
        "You've sent the most reports allowed in a day. " +
        $"You can send another <t:{retryAt.ToUnixTimeSeconds()}:R>.";

    /// <summary>
    /// The app a submitted modal is for: named by the custom id when the context had one app, read from
    /// the app dropdown when the reporter picked one inside the modal. Resolved against the apps this user
    /// may use here (<paramref name="apps"/>), not every configured one — both values echo back through
    /// the client, and another server's repository is not a valid pick here.
    /// </summary>
    private (AppConfig? App, string Repo) ResolveModalApp(string repoToken, IReadOnlyList<AppConfig> apps)
    {
        var selectValue = repoToken == ReportModal.PickAppToken
            ? ((IModalInteraction)Context.Interaction).Data.Components
                .FirstOrDefault(c => c.CustomId == ReportModal.AppSelectId)?
                .Values?.FirstOrDefault()
            : null;

        var repo = AppResolution.PickedRepo(repoToken, selectValue);
        var app = apps.FirstOrDefault(a => string.Equals(a.Repo, repo, StringComparison.OrdinalIgnoreCase));

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
            await AnswerClickAsync("Sorry — I couldn't read that button. Please run the command again.");
            return;
        }

        try
        {
            await action(id, issueNumber);
        }
        catch (ExpiredPendingReportException)
        {
            await AnswerClickAsync(ExpiredMessage);
        }
        catch (NormalizationException ex)
        {
            logger.LogWarning(ex, "Normalization failed while handling {CustomId}.", customId);
            await AnswerClickAsync(NormalizationErrorMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Component interaction {CustomId} failed.", customId);
            await AnswerClickAsync(GenericErrorMessage);
        }
    }

    /// <summary>
    /// Answers within the three-second window by replacing the clicked message with a "working" note,
    /// which also takes its buttons away — a second click on the same message becomes impossible instead
    /// of racing the first. If Discord refuses the update, a plain defer still acknowledges the click.
    /// Either way the clicked message stays the interaction's original response, which is what
    /// <see cref="AnswerClickAsync(MessageComponent)"/> later edits into the answer.
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

    private async Task ShowDraftAsync(Guid id, string? heading)
    {
        var pending = await pipeline.PeekAsync(id);
        if (pending is null)
        {
            await AnswerClickAsync(ExpiredMessage);
            return;
        }

        await AnswerClickAsync(OutcomeRenderer.RenderDraftPreview(
            new IssueDraft(pending.DraftTitle, pending.DraftBody), pending.Type, ReportPipeline.Labels(pending),
            id, heading));
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

    /// <summary>Answers a click by replacing the "working on it" note it left, never with a second message.</summary>
    private Task AnswerClickAsync(string text) => AnswerClickAsync(OutcomeRenderer.RenderText(text));

    private Task AnswerClickAsync(MessageComponent components) =>
        ClickedMessage.ReplaceAsync((IComponentInteraction)Context.Interaction, components, logger);
}
