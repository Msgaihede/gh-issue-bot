using System.Diagnostics;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordGithubBot.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot.Discord;

/// <summary>
/// Owns the gateway connection: logs in, registers the slash commands with every configured guild and
/// hands each incoming interaction to the interaction framework. It is the only place that knows about
/// service lifetimes — every interaction runs inside its own DI scope, because the pipeline and the
/// database context it uses are scoped services.
/// </summary>
public sealed class BotService(
    DiscordSocketClient client,
    InteractionService interactions,
    IServiceScopeFactory scopeFactory,
    BotOptions options,
    ILogger<BotService> logger) : BackgroundService
{
    /// <summary>
    /// The settings this layer expects from whoever constructs the <see cref="InteractionService"/>.
    /// Compiled lambdas matter here because the report flow is modal-driven, and the handlers run inline
    /// (<see cref="RunMode.Sync"/>) so that <see cref="BotService"/> can dispose an interaction's scope
    /// exactly when the interaction is done with it.
    /// </summary>
    public static InteractionServiceConfig CreateConfig() => new()
    {
        UseCompiledLambda = true,
        DefaultRunMode = RunMode.Sync,
        LogLevel = LogSeverity.Info,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.Log += LogAsync;
        interactions.Log += LogAsync;

        // Discovery happens once and its results outlive every interaction, but Discord.Net *instantiates*
        // each module while building it, so it needs a provider the module's scoped dependencies can be
        // resolved from — the root provider throws under scope validation. The scope is disposed as soon
        // as the build is done; only the reflected metadata is kept.
        using (var buildScope = scopeFactory.CreateScope())
            await interactions.AddModulesAsync(typeof(BotService).Assembly, buildScope.ServiceProvider);

        client.Ready += OnReadyAsync;
        client.InteractionCreated += OnInteractionCreatedAsync;

        await client.LoginAsync(TokenType.Bot, options.Discord.Token);
        await client.StartAsync();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a failure.
        }

        await client.StopAsync();
    }

    /// <summary>
    /// Commands are registered <em>globally</em>: a user-installed command belongs to no server, and
    /// per-server registration cannot reach a user's DMs or the servers they carry the bot into. The
    /// per-guild registrations of earlier versions are overwritten with nothing, or every configured
    /// server would list each command twice. Both steps overwrite, so a reconnect simply repeats them.
    /// </summary>
    private async Task OnReadyAsync()
    {
        logger.LogInformation("Connected to Discord as {User} ({UserId}), in {Guilds} server(s).",
            client.CurrentUser?.Username, client.CurrentUser?.Id, client.Guilds.Count);

        try
        {
            await interactions.RegisterCommandsGloballyAsync(deleteMissing: true);
            logger.LogInformation("Slash commands registered globally.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register the global slash commands.");
        }

        foreach (var guildId in options.Apps.SelectMany(a => a.GuildIds).Distinct())
        {
            try
            {
                await client.Rest.BulkOverwriteGuildCommands([], guildId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not clear legacy per-server commands in guild {GuildId}.", guildId);
            }
        }
    }

    /// <summary>
    /// Runs the interaction on its own task so the gateway keeps reading: a report takes seconds of model
    /// and GitHub calls, and the next reporter's three-second acknowledgement window must not queue behind
    /// it. The scope lives exactly as long as the handler, which is why the handlers run inline.
    /// </summary>
    private Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        _ = Task.Run(async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var what = Describe(interaction);
            var who = $"{interaction.User.Username} ({interaction.User.Id})";
            var where = Where(interaction.GuildId);
            var started = Stopwatch.GetTimestamp();

            try
            {
                var context = new SocketInteractionContext(client, interaction);
                var result = await interactions.ExecuteCommandAsync(context, scope.ServiceProvider);

                if (!result.IsSuccess)
                {
                    logger.LogWarning(
                        "{What} from {User} in {Where} failed: {Error} — {Reason}",
                        what, who, where, result.Error, result.ErrorReason);
                    await TryApologizeAsync(interaction);
                    return;
                }

                logger.LogInformation("Handled {What} from {User} in {Where} in {Seconds:0.0} s.",
                    what, who, where, Stopwatch.GetElapsedTime(started).TotalSeconds);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error while handling {What} from {User} in {Where}.", what, who, where);
                await TryApologizeAsync(interaction);
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>What the user did, in the terms they saw: <c>/issue</c>, the modal, a button or a menu.</summary>
    private static string Describe(SocketInteraction interaction) => interaction switch
    {
        SocketSlashCommand command => $"/{command.Data.Name}",
        SocketModal modal => $"the report modal ({modal.Data.CustomId})",
        SocketMessageComponent { Data.Type: ComponentType.Button } button => $"button {button.Data.CustomId}",
        SocketMessageComponent component => $"menu {component.Data.CustomId}",
        _ => $"a {interaction.Type} interaction",
    };

    /// <summary>
    /// A server the bot is in is named; a user-installed command elsewhere only has an id (or none, in DMs),
    /// because the bot is not a member there and has no cache entry to name it from.
    /// </summary>
    private string Where(ulong? guildId) => guildId switch
    {
        null => "a DM",
        { } id when client.GetGuild(id) is { } guild => $"server {guild.Name} ({id})",
        { } id => $"server {id} (user install)",
    };

    /// <summary>
    /// Last line of defence: a click the framework could not route — a button left over from an older
    /// version, say — would otherwise sit there as "interaction failed".
    /// </summary>
    private async Task TryApologizeAsync(SocketInteraction interaction)
    {
        if (interaction.HasResponded) return;

        try
        {
            await interaction.RespondAsync(
                "Sorry — I couldn't handle that. Please run the command again.", ephemeral: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not answer interaction {InteractionId}.", interaction.Id);
        }
    }

    private Task LogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };

        logger.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
