namespace FleetPulse.FleetApi.Devices;

public sealed class Site
{
    public int Id { get; set; }
    public int RegionId { get; set; }
    public required string Name { get; set; }
    public Region Region { get; set; } = null!;
    public List<Device> Devices { get; set; } = [];
}
