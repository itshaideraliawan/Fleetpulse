using FleetPulse.Contracts.DeviceProtocol;

namespace FleetPulse.Contracts.Messaging;

public sealed record DeliverCommandBatch(IReadOnlyList<DeviceCommand> Commands);
