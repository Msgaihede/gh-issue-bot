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
    IServiceProvider rootServices,
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

        // Module discovery happens once and its results outlive every interaction, so it reads from the
        // root provider rather than from a scope that is about to be disposed.
        await interactions.AddModulesAsync(typeof(BotService).Assembly, rootServices);

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
    /// Commands are registered per guild rather than globally: they appear immediately, and a guild that
    /// no app is configured for never sees them. Re-registering on a reconnect simply overwrites.
    /// </summary>
    private async Task OnReadyAsync()
    {
        var guildIds = options.Apps.SelectMany(a => a.GuildIds).Distinct().ToList();

        foreach (var guildId in guildIds)
        {
            try
            {
                await interactions.RegisterCommandsToGuildAsync(guildId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register commands for guild {GuildId}.", guildId);
            }
        }

        logger.LogInformation("Slash commands registered for {GuildCount} guild(s).", guildIds.Count);
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

            try
            {
                var context = new SocketInteractionContext(client, interaction);
                var result = await interactions.ExecuteCommandAsync(context, scope.ServiceProvider);

                if (!result.IsSuccess)
                {
                    logger.LogWarning(
                        "Interaction {InteractionId} failed: {Error} — {Reason}",
                        interaction.Id, result.Error, result.ErrorReason);
                    await TryApologizeAsync(interaction);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error while executing interaction {InteractionId}.", interaction.Id);
                await TryApologizeAsync(interaction);
            }
        });

        return Task.CompletedTask;
    }

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
