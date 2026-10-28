using FleetPulse.Contracts.DeviceProtocol;

namespace FleetPulse.Contracts.Messaging;

public sealed record DemoControlSnapshot(long Revision, bool AdmissionEnabled,
    string? BadReleaseVersion, string? BadReleaseModel, long DropGeneration,
    long ResetGeneration, DateTimeOffset? ResetAtUtc);

public sealed record DemoControlChanged(DemoControlSnapshot State);
public sealed record DemoControlApplied(DeviceMessageHeader Header, long Revision) : IDeviceMessage;

public sealed record GatewayDemoStatus(string InstanceId, long Revision, int ConnectedDevices,
    int ConfiguredDevices, DateTimeOffset ObservedAtUtc, bool AdmissionEnabled);

public static class DemoControlKeys
{
    public const string Settings = "fleetpulse:demo:settings";
    public const string Revision = "fleetpulse:demo:revision";
    public const string ResetCutoff = "fleetpulse:demo:reset-cutoff";
    public const string Gateways = "fleetpulse:demo:gateways";
    public static string Gateway(string instanceId) => $"fleetpulse:demo:gateway:{instanceId}";
}
