using FleetPulse.Contracts.Ids;

namespace FleetPulse.Contracts.DeviceProtocol;

public static class DeviceProtocolVersion
{
    public const int Current = 1;
}

public sealed record DeviceMessageHeader(
    int ProtocolVersion,
    DeviceId DeviceId,
    DateTimeOffset SentAtUtc);

public interface IDeviceMessage
{
    DeviceMessageHeader Header { get; }
}

public sealed record Hello(
    DeviceMessageHeader Header,
    string Model,
    string FirmwareVersion) : IDeviceMessage;

public enum DeviceHealth
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2
}

public sealed record Heartbeat(
    DeviceMessageHeader Header,
    DeviceHealth Health) : IDeviceMessage;

public sealed record Telemetry(
    DeviceMessageHeader Header,
    DeviceHealth Health,
    double CpuPercent,
    double MemoryPercent,
    double TemperatureCelsius) : IDeviceMessage;

public enum CommandKind
{
    RunDiagnostics = 0,
    InstallFirmware = 1,
    RollbackFirmware = 2
}

public sealed record DeviceCommand(
    DeviceMessageHeader Header,
    CommandId CommandId,
    CommandKind Kind,
    DateTimeOffset ExpiresAtUtc,
    ReleaseId? ReleaseId,
    RolloutId? RolloutId,
    WaveId? WaveId,
    string? FirmwareVersion) : IDeviceMessage;

public sealed record CommandAck(
    DeviceMessageHeader Header,
    CommandId CommandId) : IDeviceMessage;

public enum CommandOutcome
{
    Succeeded = 0,
    Failed = 1
}

public sealed record CommandResult(
    DeviceMessageHeader Header,
    CommandId CommandId,
    CommandOutcome Outcome,
    string? Detail) : IDeviceMessage;

public enum UpdateStatus
{
    Started = 0,
    Succeeded = 1,
    Failed = 2,
    RolledBack = 3
}

public sealed record UpdateReport(
    DeviceMessageHeader Header,
    CommandId CommandId,
    RolloutId RolloutId,
    WaveId WaveId,
    ReleaseId ReleaseId,
    UpdateStatus Status,
    string FirmwareVersion,
    string? Detail) : IDeviceMessage;
