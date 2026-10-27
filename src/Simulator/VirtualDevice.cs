using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;

namespace FleetPulse.Simulator;

public sealed class VirtualDevice(int sequence, DeviceId id, string model, string firmwareVersion, DeviceHealth health)
{
    private static readonly string[] Models = ["Kiosk-A", "Kiosk-B", "Kiosk-C"];
    private string _firmwareVersion = firmwareVersion;

    public int Sequence { get; } = sequence;
    public DeviceId Id { get; } = id;
    public string Model { get; } = model;
    public DeviceHealth Health { get; } = health;
    public string FirmwareVersion => Volatile.Read(ref _firmwareVersion);

    internal void SetFirmwareVersion(string version) => Volatile.Write(ref _firmwareVersion, version);

    public static bool IsKnownModel(string model) => Models.Contains(model, StringComparer.Ordinal);

    public static VirtualDevice Create(int sequence)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(sequence).CopyTo(bytes, 0);
        var profile = (sequence - 1) % 100;
        var health = profile < 90 ? DeviceHealth.Healthy
            : profile < 98 ? DeviceHealth.Degraded : DeviceHealth.Unhealthy;

        return new VirtualDevice(sequence, DeviceId.From(new Guid(bytes)),
            Models[(sequence - 1) % Models.Length], "1.0.0", health);
    }

    public Heartbeat CreateHeartbeat(DateTimeOffset now) =>
        new(new DeviceMessageHeader(DeviceProtocolVersion.Current, Id, now), Health);

    public Telemetry CreateTelemetry(DateTimeOffset now)
    {
        var baseline = Health switch
        {
            DeviceHealth.Healthy => (Cpu: 25.0, Memory: 35.0, Temperature: 35.0),
            DeviceHealth.Degraded => (Cpu: 65.0, Memory: 72.0, Temperature: 62.0),
            _ => (Cpu: 90.0, Memory: 92.0, Temperature: 82.0)
        };

        return new Telemetry(
            new DeviceMessageHeader(DeviceProtocolVersion.Current, Id, now), Health,
            baseline.Cpu + Random.Shared.NextDouble() * 5,
            baseline.Memory + Random.Shared.NextDouble() * 5,
            baseline.Temperature + Random.Shared.NextDouble() * 3);
    }
}
