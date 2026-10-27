using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.FleetApi.Commands;

namespace FleetPulse.FleetApi.Rollouts;

public sealed class WaveTarget
{
    public RolloutId RolloutId { get; set; }
    public WaveId WaveId { get; set; }
    public DeviceId DeviceId { get; set; }
    public string OriginalFirmwareVersion { get; set; } = string.Empty;
    public CommandId? InstallCommandId { get; set; }
    public CommandId? RollbackCommandId { get; set; }
    public CommandOutcome? InstallOutcome { get; set; }
    public CommandOutcome? RollbackOutcome { get; set; }
    public CommandTarget? InstallCommand { get; set; }
    public CommandTarget? RollbackCommand { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public Wave Wave { get; set; } = null!;
}
