using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Rollouts;

public sealed class RolloutQueries(FleetDbContext db)
{
    public Task<RolloutSummary[]> RecentAsync(CancellationToken cancellationToken) =>
        db.Rollouts.AsNoTracking().OrderByDescending(rollout => rollout.CreatedAtUtc)
            .ThenBy(rollout => rollout.Id).Take(20)
            .Select(rollout => new RolloutSummary(rollout.Id, rollout.Release.FirmwareVersion,
                rollout.CreatedAtUtc, rollout.Status, rollout.CurrentWaveNumber,
                rollout.Waves.Sum(wave => wave.Targets.Count)))
            .ToArrayAsync(cancellationToken);

    public async Task<RolloutProgress?> GetAsync(RolloutId id, CancellationToken cancellationToken)
    {
        var rollout = await db.Rollouts.AsNoTracking().Include(item => item.Release)
            .Include(item => item.Transitions).AsSplitQuery()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (rollout is null) return null;

        var waves = await db.Waves.AsNoTracking().Where(wave => wave.RolloutId == id)
            .OrderBy(wave => wave.Number)
            .Select(wave => new WaveProgress(wave.Number, wave.CumulativePercentage, wave.Targets.Count,
                wave.Targets.Count(target => target.InstallOutcome == CommandOutcome.Succeeded ||
                    (target.InstallOutcome == null && target.InstallCommand != null && target.InstallCommand.Status == CommandTargetStatus.Succeeded)),
                wave.Targets.Count(target => target.InstallOutcome == CommandOutcome.Failed ||
                    (target.InstallOutcome == null && target.InstallCommand != null &&
                        (target.InstallCommand.Status == CommandTargetStatus.Failed || target.InstallCommand.Status == CommandTargetStatus.Expired))),
                wave.Targets.Count(target => target.InstallCommandId != null && target.InstallOutcome == null),
                wave.GateFailures, wave.GateTotal, wave.GateCheckedAtUtc,
                wave.Targets.Count(target => target.RollbackOutcome == CommandOutcome.Succeeded),
                wave.Targets.Count(target => target.RollbackOutcome == CommandOutcome.Failed ||
                    (target.RollbackOutcome == null && target.RollbackCommand != null &&
                        (target.RollbackCommand.Status == CommandTargetStatus.Failed || target.RollbackCommand.Status == CommandTargetStatus.Expired)))))
            .ToArrayAsync(cancellationToken);
        var transitions = rollout.Transitions.OrderBy(item => item.Sequence)
            .Select(item => new RolloutTransitionView(item.Sequence, item.ToStatus,
                item.ToWaveNumber, item.Reason, item.OccurredAtUtc)).ToArray();
        return new RolloutProgress(new RolloutSummary(rollout.Id, rollout.Release.FirmwareVersion,
            rollout.CreatedAtUtc, rollout.Status, rollout.CurrentWaveNumber, waves.Sum(wave => wave.Total)),
            rollout.StateReason, transitions.FirstOrDefault(item => item.Status == RolloutStatus.Halted)?.Reason,
            waves, transitions);
    }
}

public sealed record RolloutSummary(RolloutId Id, string FirmwareVersion, DateTimeOffset CreatedAtUtc,
    RolloutStatus Status, int? CurrentWaveNumber, int Total);

public sealed record RolloutProgress(RolloutSummary Summary, string StateReason, string? HaltReason,
    IReadOnlyList<WaveProgress> Waves, IReadOnlyList<RolloutTransitionView> Transitions);

public sealed record WaveProgress(int Number, int CumulativePercentage, int Total, int Installed,
    int Failed, int UnresolvedInstalls, int? GateFailures, int? GateTotal, DateTimeOffset? GateCheckedAtUtc,
    int RolledBack, int RollbackFailed);

public sealed record RolloutTransitionView(int Sequence, RolloutStatus Status, int? WaveNumber,
    string Reason, DateTimeOffset OccurredAtUtc);
