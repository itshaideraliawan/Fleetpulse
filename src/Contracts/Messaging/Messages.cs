using FleetPulse.Contracts.DeviceProtocol;

namespace FleetPulse.Contracts.Messaging;

public sealed record DeviceHelloReceived(Hello Message);

public sealed record DeviceHeartbeatBatch(IReadOnlyList<Heartbeat> Messages);

public sealed record DeviceTelemetryBatch(IReadOnlyList<Telemetry> Messages);

public sealed record DeliverCommand(DeviceCommand Command);

public sealed record CommandAcknowledged(CommandAck Message);

public sealed record CommandCompleted(CommandResult Message);

public sealed record UpdateReported(UpdateReport Message);
