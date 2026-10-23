using FleetPulse.Contracts.Ids;

namespace FleetPulse.FleetApi.Devices;

public sealed class Device
{
    public DeviceId Id { get; set; }
    public int SiteId { get; set; }
    public required string SerialNumber { get; set; }
    public required string Model { get; set; }
    public required string FirmwareVersion { get; set; }
    public Site Site { get; set; } = null!;
}
