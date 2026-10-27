using FleetPulse.Contracts.Time;

namespace FleetPulse.FleetApi.Rollouts;

public enum RolloutTrigger
{
    Start = 0,
    WaveFinished = 1,
    GatePassed = 2,
    GateFailed = 3,
    BeginRollback = 4,
    RollbackFinished = 5
}

public sealed class RolloutStateMachine(IClock clock)
{
    public const int MaxReasonLength = 512;

    // Load all waves and their targets before starting or passing a gate.
    public bool TryApply(Rollout rollout, RolloutTrigger trigger, string reason,
        int? waveNumber = null)
    {
        ArgumentNullException.ThrowIfNull(rollout);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Length > MaxReasonLength)
        {
            throw new ArgumentException($"Reason must be at most {MaxReasonLength} characters.", nameof(reason));
        }

        if (!Enum.IsDefined(trigger)) throw new ArgumentOutOfRangeException(nameof(trigger));

        var nextWave = rollout.CurrentWaveNumber;
        RolloutStatus next;
        switch (rollout.Status, trigger)
        {
            case (RolloutStatus.Draft, RolloutTrigger.Start) when waveNumber is null:
                nextWave = NextWave(rollout, 0);
                if (nextWave is null) return false;
                next = RolloutStatus.WaveRunning;
                break;
            case (RolloutStatus.WaveRunning, RolloutTrigger.WaveFinished) when MatchesWave(rollout, waveNumber):
                next = RolloutStatus.GateCheck;
                break;
            case (RolloutStatus.GateCheck, RolloutTrigger.GatePassed) when MatchesWave(rollout, waveNumber):
                var followingWave = NextWave(rollout, waveNumber!.Value);
                next = followingWave is null ? RolloutStatus.Completed : RolloutStatus.WaveRunning;
                nextWave = followingWave ?? nextWave;
                break;
            case (RolloutStatus.GateCheck, RolloutTrigger.GateFailed) when MatchesWave(rollout, waveNumber):
                next = RolloutStatus.Halted;
                break;
            case (RolloutStatus.Halted, RolloutTrigger.BeginRollback) when waveNumber is null:
                next = RolloutStatus.RollingBack;
                break;
            case (RolloutStatus.RollingBack, RolloutTrigger.RollbackFinished) when waveNumber is null:
                next = RolloutStatus.RolledBack;
                break;
            default:
                return false;
        }

        rollout.RecordTransition(next, nextWave, reason.Trim(), clock.UtcNow);
        return true;
    }

    private static bool MatchesWave(Rollout rollout, int? waveNumber) =>
        waveNumber is not null && waveNumber == rollout.CurrentWaveNumber;

    private static int? NextWave(Rollout rollout, int afterNumber) => rollout.Waves
        .Where(wave => wave.Number > afterNumber && wave.Targets.Count > 0)
        .OrderBy(wave => wave.Number).Select(wave => (int?)wave.Number).FirstOrDefault();
}
