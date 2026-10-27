using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using FleetPulse.FleetApi.Rollouts;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace FleetPulse.IntegrationTests;

public sealed class RolloutPlanningTests
{
    private static readonly int[] FleetWaveSizes = [50, 450, 2000, 2500];

    [Fact]
    public async Task PersistsFleetPlanAndRejectsInvalidOrDuplicateMembership()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await sql.StartAsync(timeout.Token);
        var options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlServer(sql.GetConnectionString()).Options;
        var clock = new TestClock();
        var releaseId = ReleaseId.New();
        RolloutId rolloutId;
        DeviceId[] deviceIds;
        await using (var db = new FleetDbContext(options))
        {
            await db.Database.MigrateAsync(timeout.Token);
            await FleetSeed.SeedAsync(db, timeout.Token);
            db.Releases.Add(new Release { Id = releaseId, FirmwareVersion = "2.4.0", CreatedAtUtc = clock.UtcNow });
            await db.SaveChangesAsync(timeout.Token);
            deviceIds = await db.Devices.Select(device => device.Id).ToArrayAsync(timeout.Token);
            var planner = new RolloutPlanner(db, clock);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                planner.CreateAsync(ReleaseId.New(), deviceIds, timeout.Token));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                planner.CreateAsync(releaseId, [DeviceId.New()], timeout.Token));
            Assert.Equal(0, await db.Rollouts.CountAsync(timeout.Token));
            var rollout = await planner.CreateAsync(releaseId, deviceIds, timeout.Token);
            rolloutId = rollout.Id;
        }

        WaveId firstWaveId;
        await using (var db = new FleetDbContext(options))
        {
            var rollout = await db.Rollouts.Include(item => item.Release)
                .Include(item => item.Waves).ThenInclude(wave => wave.Targets)
                .SingleAsync(item => item.Id == rolloutId, timeout.Token);
            Assert.Equal(RolloutStatus.Draft, rollout.Status);
            Assert.False(string.IsNullOrWhiteSpace(rollout.StateReason));
            Assert.Equal(clock.UtcNow, rollout.CreatedAtUtc);
            Assert.Equal("2.4.0", rollout.Release.FirmwareVersion);
            var waves = rollout.Waves.OrderBy(wave => wave.Number).ToArray();
            firstWaveId = waves[0].Id;
            Assert.Equal(FleetWaveSizes, waves.Select(wave => wave.Targets.Count));
            Assert.Equal(5000, waves.SelectMany(wave => wave.Targets).Select(target => target.DeviceId).Distinct().Count());
            Assert.All(waves.SelectMany(wave => wave.Targets), target => Assert.Equal("1.0.0", target.OriginalFirmwareVersion));
            var device = await db.Devices.FirstAsync(timeout.Token);
            device.FirmwareVersion = "2.4.0";
            await db.SaveChangesAsync(timeout.Token);
        }

        await using (var db = new FleetDbContext(options))
        {
            Assert.All(await db.WaveTargets.AsNoTracking().ToArrayAsync(timeout.Token),
                target => Assert.Equal("1.0.0", target.OriginalFirmwareVersion));
            db.WaveTargets.Add(new WaveTarget
            {
                RolloutId = rolloutId, WaveId = firstWaveId, DeviceId = deviceIds[0],
                OriginalFirmwareVersion = "1.0.0"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(timeout.Token));
        }

        RolloutId otherRolloutId;
        await using (var db = new FleetDbContext(options))
        {
            var other = await new RolloutPlanner(db, clock).CreateAsync(releaseId, [deviceIds[0]], timeout.Token);
            otherRolloutId = other.Id;
            Assert.Equal(4, other.Waves.Count);
            Assert.Equal(1, other.Waves.Sum(wave => wave.Targets.Count));
        }

        await using (var db = new FleetDbContext(options))
        {
            db.WaveTargets.Add(new WaveTarget
            {
                RolloutId = otherRolloutId, WaveId = firstWaveId, DeviceId = deviceIds[1],
                OriginalFirmwareVersion = "1.0.0"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(timeout.Token));
        }
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    }
}
