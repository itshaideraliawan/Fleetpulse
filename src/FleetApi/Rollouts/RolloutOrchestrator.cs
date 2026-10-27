using System.Globalization;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Rollouts;

public sealed class RolloutOrchestrator(IDbContextOutbox<FleetDbContext> outbox,
    RolloutStateMachine machine, IClock clock)
{
    public static readonly TimeSpan CommandLifetime = TimeSpan.FromMinutes(5);

    public async Task AdvanceAsync(RolloutId id, CancellationToken cancellationToken)
    {
        var db = outbox.DbContext;
        var rollout = await db.Rollouts.Include(item => item.Release)
            .Include(item => item.Waves).ThenInclude(wave => wave.Targets).ThenInclude(target => target.InstallCommand)
            .Include(item => item.Waves).ThenInclude(wave => wave.Targets).ThenInclude(target => target.RollbackCommand)
            .AsSplitQuery().SingleAsync(item => item.Id == id, cancellationToken);
        var now = clock.UtcNow;
        switch (rollout.Status)
        {
            case RolloutStatus.Draft:
                if (machine.TryApply(rollout, RolloutTrigger.Start, "Starting the first nonempty wave"))
                    await QueueInstallsAsync(rollout, now, cancellationToken);
                break;
            case RolloutStatus.WaveRunning:
            case RolloutStatus.GateCheck:
                var wave = rollout.Waves.Single(item => item.Number == rollout.CurrentWaveNumber);
                if (wave.Targets.Any(target => target.InstallCommandId is null))
                {
                    await QueueInstallsAsync(rollout, now, cancellationToken);
                    break;
                }
                var gate = HealthGate.Evaluate(wave.Targets.Select(EffectiveStatus).ToArray());
                if (!gate.IsComplete) break;
                if (rollout.Status == RolloutStatus.WaveRunning)
                    machine.TryApply(rollout, RolloutTrigger.WaveFinished, "All wave commands reached a terminal state", wave.Number);
                wave.GateFailures = gate.Failures;
                wave.GateTotal = gate.Total;
                wave.GateCheckedAtUtc = now;
                var reason = string.Create(CultureInfo.InvariantCulture,
                    $"Wave {wave.Number}: {gate.Failures}/{gate.Total} updates failed or expired; required failure rate < 2%");
                machine.TryApply(rollout, gate.Passed ? RolloutTrigger.GatePassed : RolloutTrigger.GateFailed, reason, wave.Number);
                if (rollout.Status == RolloutStatus.WaveRunning)
                    await QueueInstallsAsync(rollout, now, cancellationToken);
                if (rollout.Status == RolloutStatus.Halted)
                    await BeginRollbackAsync(rollout, now, cancellationToken);
                break;
            case RolloutStatus.Halted:
                await BeginRollbackAsync(rollout, now, cancellationToken);
                break;
            case RolloutStatus.RollingBack:
                await QueueRollbacksAsync(rollout, now, cancellationToken);
                var issued = rollout.Waves.SelectMany(wave => wave.Targets)
                    .Where(target => target.InstallCommandId is not null).ToArray();
                // An expired install is uncertain until a definitive device result arrives.
                if (issued.All(target => target.InstallOutcome is not null) &&
                    issued.Where(IsUpdated).All(target => target.RollbackOutcome == CommandOutcome.Succeeded))
                    machine.TryApply(rollout, RolloutTrigger.RollbackFinished, "Every updated device confirmed restoration of its original firmware");
                break;
        }
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    private async Task BeginRollbackAsync(Rollout rollout, DateTimeOffset now, CancellationToken cancellationToken)
    {
        machine.TryApply(rollout, RolloutTrigger.BeginRollback, "Health gate failed; restoring every updated device across all waves");
        await QueueRollbacksAsync(rollout, now, cancellationToken);
    }

    private Task QueueInstallsAsync(Rollout rollout, DateTimeOffset now, CancellationToken cancellationToken) =>
        QueueAsync(rollout, rollout.Waves.Single(wave => wave.Number == rollout.CurrentWaveNumber).Targets
            .Where(target => target.InstallCommandId is null).ToArray(), CommandKind.InstallFirmware, now, cancellationToken);

    private Task QueueRollbacksAsync(Rollout rollout, DateTimeOffset now, CancellationToken cancellationToken) =>
        QueueAsync(rollout, rollout.Waves.SelectMany(wave => wave.Targets)
            .Where(target => IsUpdated(target) && target.RollbackCommandId is null).ToArray(),
            CommandKind.RollbackFirmware, now, cancellationToken);

    private async Task QueueAsync(Rollout rollout, WaveTarget[] targets, CommandKind kind,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (targets.Length == 0) return;
        var command = new Command { Id = Guid.NewGuid(), Kind = kind, CreatedAtUtc = now, ExpiresAtUtc = now + CommandLifetime };
        outbox.DbContext.Commands.Add(command);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delivery = new CommandTarget
            {
                Id = CommandId.New(), Command = command, DeviceId = target.DeviceId,
                CreatedAtUtc = now, LastSentAtUtc = now, ReleaseId = rollout.ReleaseId,
                RolloutId = rollout.Id, WaveId = target.WaveId,
                FirmwareVersion = kind == CommandKind.InstallFirmware ? rollout.Release.FirmwareVersion : target.OriginalFirmwareVersion
            };
            outbox.DbContext.CommandTargets.Add(delivery);
            if (kind == CommandKind.InstallFirmware) target.InstallCommand = delivery;
            else target.RollbackCommand = delivery;
            await outbox.PublishAsync(new DeliverCommand(delivery.ToDeviceCommand(now)));
        }
    }

    private static bool IsUpdated(WaveTarget target) => target.InstallOutcome == CommandOutcome.Succeeded ||
        target.InstallCommand?.Status == CommandTargetStatus.Succeeded;

    private static CommandTargetStatus EffectiveStatus(WaveTarget target) => target.InstallOutcome switch
    {
        CommandOutcome.Succeeded => CommandTargetStatus.Succeeded,
        CommandOutcome.Failed => CommandTargetStatus.Failed,
        _ => target.InstallCommand!.Status
    };
}
