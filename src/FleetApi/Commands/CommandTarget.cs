using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.DeviceProtocol;

namespace FleetPulse.FleetApi.Commands;

public enum CommandTargetStatus
{
    Pending = 0,
    Acknowledged = 1,
    Succeeded = 2,
    Failed = 3,
    Expired = 4
}

public sealed class CommandTarget
{
    public CommandId Id { get; set; }
    public Guid CommandGroupId { get; set; }
    public DeviceId DeviceId { get; set; }
    public CommandTargetStatus Status { get; set; } = CommandTargetStatus.Pending;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSentAtUtc { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? ResultDetail { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ReleaseId? ReleaseId { get; set; }
    public RolloutId? RolloutId { get; set; }
    public WaveId? WaveId { get; set; }
    public string? FirmwareVersion { get; set; }
    public Command Command { get; set; } = null!;

    public DeviceCommand ToDeviceCommand(DateTimeOffset sentAtUtc) => new(
        new DeviceMessageHeader(DeviceProtocolVersion.Current, DeviceId, sentAtUtc),
        Id, Command.Kind, Command.ExpiresAtUtc, ReleaseId, RolloutId, WaveId, FirmwareVersion);
}
