using FleetPulse.Contracts.Ids;

namespace FleetPulse.FleetApi.Rollouts;

public enum RolloutStatus
{
    Draft = 0,
    WaveRunning = 1,
    GateCheck = 2,
    Completed = 3,
    Halted = 4,
    RollingBack = 5,
    RolledBack = 6
}

public sealed class Rollout
{
    private readonly List<RolloutTransition> _transitions = [];

    public RolloutId Id { get; set; }
    public ReleaseId ReleaseId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public RolloutStatus Status { get; private set; } = RolloutStatus.Draft;
    public string StateReason { get; private set; } = "Rollout created with planned waves";
    public int? CurrentWaveNumber { get; private set; }
    public int TransitionVersion { get; private set; }
    public IReadOnlyCollection<RolloutTransition> Transitions => _transitions.AsReadOnly();
    public Release Release { get; set; } = null!;
    public ICollection<Wave> Waves { get; } = new List<Wave>();

    internal void RecordTransition(RolloutStatus next, int? nextWaveNumber,
        string reason, DateTimeOffset occurredAtUtc)
    {
        var sequence = checked(TransitionVersion + 1);
        _transitions.Add(new RolloutTransition(Id, sequence, Status, next,
            CurrentWaveNumber, nextWaveNumber, reason, occurredAtUtc));
        Status = next;
        CurrentWaveNumber = nextWaveNumber;
        StateReason = reason;
        TransitionVersion = sequence;
    }
}
