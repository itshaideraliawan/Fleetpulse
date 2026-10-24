using FleetPulse.Contracts.Ids;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Devices;

public static class FleetSeed
{
    public const int RegionCount = 4;
    public const int SitesPerRegion = 10;
    public const int DevicesPerSite = 125;
    public const int DeviceCount = RegionCount * SitesPerRegion * DevicesPerSite;

    private static readonly string[] RegionNames = ["North", "South", "East", "West"];
    private static readonly string[] Models = ["Kiosk-A", "Kiosk-B", "Kiosk-C"];

    public static async Task SeedAsync(FleetDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Regions.AnyAsync(cancellationToken))
        {
            return;
        }

        for (var regionIndex = 0; regionIndex < RegionCount; regionIndex++)
        {
            var region = new Region { Id = regionIndex + 1, Name = RegionNames[regionIndex] };
            for (var siteIndex = 0; siteIndex < SitesPerRegion; siteIndex++)
            {
                var siteNumber = regionIndex * SitesPerRegion + siteIndex + 1;
                var site = new Site
                {
                    Id = siteNumber,
                    Name = $"{region.Name} Site {siteIndex + 1:00}",
                    Region = region
                };

                for (var deviceIndex = 0; deviceIndex < DevicesPerSite; deviceIndex++)
                {
                    var sequence = (siteNumber - 1) * DevicesPerSite + deviceIndex + 1;
                    var guidBytes = new byte[16];
                    BitConverter.GetBytes(sequence).CopyTo(guidBytes, 0);
                    site.Devices.Add(new Device
                    {
                        Id = DeviceId.From(new Guid(guidBytes)),
                        SerialNumber = $"FP-{sequence:00000}",
                        Model = Models[(sequence - 1) % Models.Length],
                        FirmwareVersion = "1.0.0",
                        Site = site
                    });
                }

                region.Sites.Add(site);
            }

            db.Regions.Add(region);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
