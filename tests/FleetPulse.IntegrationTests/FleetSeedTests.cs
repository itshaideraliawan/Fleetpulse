using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace FleetPulse.IntegrationTests;

public sealed class FleetSeedTests
{
    [Fact]
    public async Task SeedCreatesExpectedFleetAndCanRunTwice()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await sqlServer.StartAsync(timeout.Token);

        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlServer(sqlServer.GetConnectionString())
            .Options;

        await using var db = new FleetDbContext(options);
        await db.Database.MigrateAsync(timeout.Token);
        await FleetSeed.SeedAsync(db, timeout.Token);
        await FleetSeed.SeedAsync(db, timeout.Token);

        Assert.Equal(4, await db.Regions.CountAsync(timeout.Token));
        Assert.Equal(40, await db.Sites.CountAsync(timeout.Token));
        Assert.Equal(5_000, await db.Devices.CountAsync(timeout.Token));
        Assert.Equal(3, await db.Devices.Select(device => device.Model).Distinct().CountAsync(timeout.Token));
    }
}
