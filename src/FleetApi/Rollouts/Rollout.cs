using FleetPulse.Contracts.Ids;

namespace FleetPulse.FleetApi.Rollouts;

public enum RolloutStatus
{
    Draft = 0
}

public sealed class Rollout
{
    public RolloutId Id { get; set; }
    public ReleaseId ReleaseId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public RolloutStatus Status { get; private set; } = RolloutStatus.Draft;
    public string StateReason { get; private set; } = "Rollout created with planned waves";
    public Release Release { get; set; } = null!;
    public ICollection<Wave> Waves { get; } = new List<Wave>();
}
