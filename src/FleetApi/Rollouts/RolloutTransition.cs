using FleetPulse.Contracts.Ids;

namespace FleetPulse.FleetApi.Rollouts;

public sealed class RolloutTransition
{
    private RolloutTransition() { }

    internal RolloutTransition(RolloutId rolloutId, int sequence,
        RolloutStatus fromStatus, RolloutStatus toStatus, int? fromWaveNumber,
        int? toWaveNumber, string reason, DateTimeOffset occurredAtUtc)
    {
        Id = Guid.NewGuid();
        RolloutId = rolloutId;
        Sequence = sequence;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        FromWaveNumber = fromWaveNumber;
        ToWaveNumber = toWaveNumber;
        Reason = reason;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }
    public RolloutId RolloutId { get; private set; }
    public int Sequence { get; private set; }
    public RolloutStatus FromStatus { get; private set; }
    public RolloutStatus ToStatus { get; private set; }
    public int? FromWaveNumber { get; private set; }
    public int? ToWaveNumber { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }
}
