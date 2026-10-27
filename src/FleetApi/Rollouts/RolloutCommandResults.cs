using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Rollouts;

public static class RolloutCommandResults
{
    public static async Task<bool> RecordAsync(CommandTarget command, CommandOutcome outcome,
        FleetDbContext db, CancellationToken cancellationToken)
    {
        if (command.RolloutId is null || !Enum.IsDefined(outcome)) return false;
        var target = await db.WaveTargets.Include(item => item.Wave).ThenInclude(wave => wave.Rollout)
            .SingleOrDefaultAsync(item => item.RolloutId == command.RolloutId &&
                item.DeviceId == command.DeviceId, cancellationToken);
        if (target is null || target.WaveId != command.WaveId ||
            target.Wave.Rollout.ReleaseId != command.ReleaseId) return false;

        if (command.Command.Kind == CommandKind.InstallFirmware && target.InstallCommandId == command.Id &&
            target.InstallOutcome is null)
        {
            target.InstallOutcome = outcome;
        }
        else if (command.Command.Kind == CommandKind.RollbackFirmware && target.RollbackCommandId == command.Id &&
            target.RollbackOutcome is null)
        {
            target.RollbackOutcome = outcome;
        }
        else return false;

        if (outcome == CommandOutcome.Succeeded && command.FirmwareVersion is { } version)
        {
            var device = await db.Devices.SingleAsync(item => item.Id == target.DeviceId, cancellationToken);
            device.FirmwareVersion = version;
        }
        return true;
    }
}
