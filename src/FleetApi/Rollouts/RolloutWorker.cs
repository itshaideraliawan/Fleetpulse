using FleetPulse.Contracts.Ids;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Rollouts;

public sealed partial class RolloutWorker(IServiceScopeFactory scopes, ILogger<RolloutWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await ScanAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var lookup = scopes.CreateAsyncScope();
            var db = lookup.ServiceProvider.GetRequiredService<FleetDbContext>();
            var ids = await db.Rollouts.AsNoTracking()
                .Where(rollout => rollout.Status != RolloutStatus.Completed && rollout.Status != RolloutStatus.RolledBack)
                .OrderBy(rollout => rollout.CreatedAtUtc).Select(rollout => rollout.Id).ToArrayAsync(cancellationToken);
            foreach (var id in ids)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<RolloutOrchestrator>().AdvanceAsync(id, cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    LogRolloutFailure(logger, id, exception);
                }
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogScanFailure(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Rollout {RolloutId} advancement failed; retrying on next tick")]
    private static partial void LogRolloutFailure(ILogger logger, RolloutId rolloutId, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rollout scan failed; retrying on next tick")]
    private static partial void LogScanFailure(ILogger logger, Exception exception);
}
