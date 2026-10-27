using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace FleetPulse.FleetApi.Commands;

public sealed partial class CommandRetryWorker(
    IServiceScopeFactory scopeFactory, IClock clock, ILogger<CommandRetryWorker> logger)
    : BackgroundService
{
    public const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ScanAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogScanFailure(logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<FleetDbContext>>();
            var db = outbox.DbContext;
            var now = clock.UtcNow;
            var retryBefore = now - CommandTargetStateMachine.RetryInterval;
            var targets = await db.CommandTargets.Include(target => target.Command)
                .Where(target =>
                    (target.Status == CommandTargetStatus.Pending &&
                        target.LastSentAtUtc <= retryBefore) ||
                    ((target.Status == CommandTargetStatus.Pending ||
                        target.Status == CommandTargetStatus.Acknowledged) &&
                        target.Command.ExpiresAtUtc <= now))
                .OrderBy(target => target.LastSentAtUtc)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken);

            if (targets.Length == 0)
            {
                return;
            }

            foreach (var target in targets)
            {
                if (CommandTargetStateMachine.Expire(target, target.Command.ExpiresAtUtc, now))
                {
                    continue;
                }

                if (!CommandTargetStateMachine.Retry(target, target.Command.ExpiresAtUtc, now))
                {
                    continue;
                }

                var message = new DeviceCommand(
                    new DeviceMessageHeader(DeviceProtocolVersion.Current, target.DeviceId, now),
                    target.Id, target.Command.Kind, target.Command.ExpiresAtUtc,
                    null, null, null, null);
                await outbox.PublishAsync(new DeliverCommand(message));
            }

            await outbox.SaveChangesAndFlushMessagesAsync(MultiFlushMode.AllowMultiples,
                cancellationToken);

            if (targets.Length < BatchSize)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Command retry scan failed; retrying on next tick")]
    private static partial void LogScanFailure(ILogger logger, Exception exception);
}
