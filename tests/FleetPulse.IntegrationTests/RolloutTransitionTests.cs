using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using FleetPulse.FleetApi.Rollouts;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace FleetPulse.IntegrationTests;

public sealed class RolloutTransitionTests
{
    [Fact]
    public async Task PersistsOrderedReasonsAndRejectsCompetingTransitionsAtomically()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await sql.StartAsync(timeout.Token);
        var options = new DbContextOptionsBuilder<FleetDbContext>().UseSqlServer(sql.GetConnectionString()).Options;
        var clock = new TestClock();
        var machine = new RolloutStateMachine(clock);
        RolloutId id;
        await using (var db = new FleetDbContext(options))
        {
            await db.Database.MigrateAsync(timeout.Token);
            var release = new Release { Id = ReleaseId.New(), FirmwareVersion = "2.4.0", CreatedAtUtc = clock.UtcNow };
            var site = new Site { Id = 1, Name = "Test", Region = new Region { Id = 1, Name = "North" } };
            var deviceIds = new[] { DeviceId.New(), DeviceId.New() };
            for (var index = 0; index < deviceIds.Length; index++)
            {
                db.Devices.Add(new Device
                {
                    Id = deviceIds[index], SerialNumber = $"FP-{index:00000}", Model = "Kiosk-A",
                    FirmwareVersion = "1.0.0", Site = site
                });
            }
            db.Releases.Add(release);
            await db.SaveChangesAsync(timeout.Token);
            id = (await new RolloutPlanner(db, clock).CreateAsync(release.Id, deviceIds, timeout.Token)).Id;
        }

        await using (var first = new FleetDbContext(options))
        await using (var second = new FleetDbContext(options))
        {
            var winner = await LoadPlan(first, id).SingleAsync(timeout.Token);
            var stale = await LoadPlan(second, id).SingleAsync(timeout.Token);
            Assert.True(machine.TryApply(winner, RolloutTrigger.Start, "Operator approved"));
            Assert.True(machine.TryApply(stale, RolloutTrigger.Start, "Competing start"));
            await first.SaveChangesAsync(timeout.Token);
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => second.SaveChangesAsync(timeout.Token));
        }

        await using (var db = new FleetDbContext(options))
        {
            var rollout = await LoadPlan(db, id).Include(item => item.Transitions)
                .AsSplitQuery().SingleAsync(timeout.Token);
            Assert.Equal(RolloutStatus.WaveRunning, rollout.Status);
            Assert.Equal("Operator approved", rollout.StateReason);
            Assert.Equal(1, rollout.TransitionVersion);
            Assert.Single(rollout.Transitions);
            Assert.True(machine.TryApply(rollout, RolloutTrigger.WaveFinished, "First wave finished", 1));
            Assert.True(machine.TryApply(rollout, RolloutTrigger.GatePassed, "First gate passed", 1));
            Assert.Equal(4, rollout.CurrentWaveNumber);
            await db.SaveChangesAsync(timeout.Token);
        }

        await using (var db = new FleetDbContext(options))
        {
            // History is deliberately not loaded; sequencing comes from the persisted version.
            var rollout = await LoadPlan(db, id).SingleAsync(timeout.Token);
            Assert.True(machine.TryApply(rollout, RolloutTrigger.WaveFinished, "Last wave finished", 4));
            Assert.True(machine.TryApply(rollout, RolloutTrigger.GateFailed, "Wave health gate failed", 4));
            await db.SaveChangesAsync(timeout.Token);
        }

        await using (var db = new FleetDbContext(options))
        {
            var rollout = await db.Rollouts.SingleAsync(item => item.Id == id, timeout.Token);
            Assert.True(machine.TryApply(rollout, RolloutTrigger.BeginRollback, "Restoring updated devices"));
            Assert.True(machine.TryApply(rollout, RolloutTrigger.RollbackFinished, "All updated devices restored"));
            await db.SaveChangesAsync(timeout.Token);
        }

        await using (var db = new FleetDbContext(options))
        {
            var rollout = await db.Rollouts.Include(item => item.Transitions)
                .SingleAsync(item => item.Id == id, timeout.Token);
            Assert.Equal(RolloutStatus.RolledBack, rollout.Status);
            Assert.Equal("All updated devices restored", rollout.StateReason);
            Assert.Equal(7, rollout.TransitionVersion);
            var history = rollout.Transitions.OrderBy(item => item.Sequence).ToArray();
            Assert.Equal(Enumerable.Range(1, 7), history.Select(item => item.Sequence));
            Assert.All(history, item => Assert.Equal(clock.UtcNow, item.OccurredAtUtc));
            Assert.Equal(RolloutStatus.Draft, history[0].FromStatus);
            Assert.Equal(1, history[0].ToWaveNumber);
            Assert.Equal(1, history[2].FromWaveNumber);
            Assert.Equal(4, history[2].ToWaveNumber);
            Assert.Equal("Wave health gate failed", history[4].Reason);
            Assert.DoesNotContain(history, item => item.Reason == "Competing start");
            Assert.False(machine.TryApply(rollout, RolloutTrigger.RollbackFinished, "Duplicate result"));
            Assert.Equal(0, await db.SaveChangesAsync(timeout.Token));
        }
    }

    private static IQueryable<Rollout> LoadPlan(FleetDbContext db, RolloutId id) =>
        db.Rollouts.Where(item => item.Id == id).Include(item => item.Waves).ThenInclude(wave => wave.Targets);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    }
}
