using FleetPulse.Contracts.Ids;

namespace FleetPulse.FleetApi.Rollouts;

public sealed class WaveTarget
{
    public RolloutId RolloutId { get; set; }
    public WaveId WaveId { get; set; }
    public DeviceId DeviceId { get; set; }
    public string OriginalFirmwareVersion { get; set; } = string.Empty;
    public Wave Wave { get; set; } = null!;
}
