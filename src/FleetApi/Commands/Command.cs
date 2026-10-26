using FleetPulse.Contracts.DeviceProtocol;

namespace FleetPulse.FleetApi.Commands;

public sealed class Command
{
    public Guid Id { get; set; }
    public CommandKind Kind { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public ICollection<CommandTarget> Targets { get; } = new List<CommandTarget>();
}
