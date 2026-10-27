using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Commands;

public static class CommandReportHandler
{
    public static Task HandleAsync(CommandAcknowledged report, FleetDbContext db,
        IClock clock, CancellationToken cancellationToken) =>
        ApplyAsync(report.Message.CommandId, report.Message.Header.DeviceId, db, clock,
            (target, expires, now) => CommandTargetStateMachine.Acknowledge(target, expires, now),
            cancellationToken);

    public static Task HandleAsync(CommandCompleted report, FleetDbContext db,
        IClock clock, CancellationToken cancellationToken) =>
        ApplyAsync(report.Message.CommandId, report.Message.Header.DeviceId, db, clock,
            (target, expires, now) => CommandTargetStateMachine.Complete(target, expires,
                report.Message.Outcome, report.Message.Detail, now), cancellationToken);

    private static async Task ApplyAsync(CommandId commandId, DeviceId deviceId,
        FleetDbContext db, IClock clock,
        Func<CommandTarget, DateTimeOffset, DateTimeOffset, bool> transition,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var target = await db.CommandTargets.Include(item => item.Command)
                .SingleOrDefaultAsync(item => item.Id == commandId && item.DeviceId == deviceId,
                    cancellationToken);
            if (target is null)
            {
                return;
            }

            var now = clock.UtcNow;
            var changed = transition(target, target.Command.ExpiresAtUtc, now) ||
                CommandTargetStateMachine.Expire(target, target.Command.ExpiresAtUtc, now);
            if (!changed)
            {
                return;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 2)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}
