using FleetPulse.Contracts.Ids;

namespace FleetPulse.FleetApi.Rollouts;

public sealed class Wave
{
    public WaveId Id { get; set; }
    public RolloutId RolloutId { get; set; }
    public int Number { get; set; }
    public int CumulativePercentage { get; set; }
    public Rollout Rollout { get; set; } = null!;
    public ICollection<WaveTarget> Targets { get; } = new List<WaveTarget>();
}
