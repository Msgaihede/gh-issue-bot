using DiscordGithubBot.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiscordGithubBot;

/// <summary>
/// Hourly cleanup of expired pending reports. Every pass runs in its own DI scope because
/// <see cref="IPendingReportStore"/> and the database context behind it are scoped services, and a
/// failed pass is a warning rather than a crash — the next tick simply tries again.
/// </summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, ILogger<MaintenanceService> logger)
    : BackgroundService
{
    /// <summary>Pending reports live for an hour, so sweeping once an hour is enough.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var removed = await scope.ServiceProvider.GetRequiredService<IPendingReportStore>()
                        .CleanupExpiredAsync(stoppingToken);
                    if (removed > 0) logger.LogInformation("Cleaned up {Count} expired pending reports", removed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogWarning(ex, "Pending-report cleanup failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a failure.
        }
    }
}
