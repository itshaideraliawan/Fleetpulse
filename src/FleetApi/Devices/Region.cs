namespace FleetPulse.FleetApi.Devices;

public sealed class Region
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Site> Sites { get; set; } = [];
}
