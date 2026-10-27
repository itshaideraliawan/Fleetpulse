using FleetPulse.Contracts.Ids;

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
    public Command Command { get; set; } = null!;
}
