using FleetPulse.Contracts.Ids;

namespace FleetPulse.FleetApi.Rollouts;

public sealed class Release
{
    public ReleaseId Id { get; set; }
    public string FirmwareVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public ICollection<Rollout> Rollouts { get; } = new List<Rollout>();
}
