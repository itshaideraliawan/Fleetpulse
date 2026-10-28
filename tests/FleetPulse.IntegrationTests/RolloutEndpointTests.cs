using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Devices;
using FleetPulse.FleetApi.Rollouts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace FleetPulse.IntegrationTests;

public sealed class RolloutEndpointTests
{
    [Fact]
    public async Task LaunchAndProgressExposeWavesHaltAndRollbackWithoutOverlappingCohorts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await sql.StartAsync(timeout.Token);
        var clock = new TestClock();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<FleetDbContext>(options => options.UseSqlServer(sql.GetConnectionString()));
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddScoped<RolloutPlanner>();
        builder.Services.AddScoped<RolloutQueries>();
        builder.Services.AddFleetOpenApi();
        await using var app = builder.Build();
        app.MapRolloutEndpoints();
        app.MapOpenApi();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            await db.Database.MigrateAsync(timeout.Token);
            await FleetSeed.SeedAsync(db, timeout.Token);
        }
        await app.StartAsync(timeout.Token);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        using var invalid = await http.PostAsJsonAsync("/api/rollouts",
            new CreateRolloutRequest(" ", 0, "", 0), timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        using var noMatch = await http.PostAsJsonAsync("/api/rollouts",
            new CreateRolloutRequest("2.4.0", null, "unknown", 100), timeout.Token);
        Assert.Equal(HttpStatusCode.NotFound, noMatch.StatusCode);

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => http.PostAsJsonAsync(
            "/api/rollouts", new CreateRolloutRequest(" 2.4.0 ", null, null, 100), timeout.Token)));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var accepted = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        var created = await accepted.Content.ReadFromJsonAsync<RolloutSummary>(timeout.Token);
        Assert.NotNull(created);
        Assert.Equal(clock.UtcNow, created.CreatedAtUtc);
        Assert.Equal("2.4.0", created.FirmwareVersion);
        Assert.Equal(100, created.Total);
        Assert.Equal(RolloutStatus.Draft, created.Status);
        Assert.Equal($"/api/rollouts/{created.Id.Value}", accepted.Headers.Location?.ToString());
        foreach (var response in responses) response.Dispose();

        var initial = await http.GetFromJsonAsync<RolloutProgress>($"/api/rollouts/{created.Id.Value}", timeout.Token);
        Assert.NotNull(initial);
        Assert.Equal([1, 9, 40, 50], initial.Waves.Select(wave => wave.Total));
        Assert.All(initial.Waves, wave => Assert.Null(wave.GateCheckedAtUtc));
        Assert.Null(initial.HaltReason);
        var expiredId = CommandId.New();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            Assert.Equal(1, await db.Releases.CountAsync(timeout.Token));
            Assert.Equal(1, await db.Rollouts.CountAsync(timeout.Token));
            var rollout = await db.Rollouts.Include(item => item.Waves).ThenInclude(wave => wave.Targets)
                .SingleAsync(timeout.Token);
            var machine = new RolloutStateMachine(clock);
            Assert.True(machine.TryApply(rollout, RolloutTrigger.Start, "Start"));
            Assert.True(machine.TryApply(rollout, RolloutTrigger.WaveFinished, "Canary finished", 1));
            Assert.True(machine.TryApply(rollout, RolloutTrigger.GatePassed, "Canary passed", 1));
            var first = rollout.Waves.Single(wave => wave.Number == 1);
            first.GateFailures = 0; first.GateTotal = 1; first.GateCheckedAtUtc = clock.UtcNow;
            first.Targets.Single().InstallOutcome = CommandOutcome.Succeeded;
            first.Targets.Single().RollbackOutcome = CommandOutcome.Succeeded;
            var second = rollout.Waves.Single(wave => wave.Number == 2);
            second.GateFailures = 3; second.GateTotal = 9; second.GateCheckedAtUtc = clock.UtcNow;
            var targets = second.Targets.ToArray();
            for (var index = 0; index < targets.Length; index++)
            {
                targets[index].InstallOutcome = index < 6 ? CommandOutcome.Succeeded : CommandOutcome.Failed;
                if (index < 2) targets[index].RollbackOutcome = CommandOutcome.Succeeded;
            }
            var command = new Command
            {
                Id = Guid.NewGuid(), Kind = CommandKind.InstallFirmware,
                CreatedAtUtc = clock.UtcNow, ExpiresAtUtc = clock.UtcNow.AddMinutes(5)
            };
            command.Targets.Add(new CommandTarget
            {
                Id = expiredId, DeviceId = targets[8].DeviceId, Status = CommandTargetStatus.Expired,
                CreatedAtUtc = clock.UtcNow, LastSentAtUtc = clock.UtcNow
            });
            db.Commands.Add(command);
            targets[8].InstallCommandId = expiredId;
            targets[8].InstallOutcome = null;
            Assert.True(machine.TryApply(rollout, RolloutTrigger.WaveFinished, "Wave finished", 2));
            Assert.True(machine.TryApply(rollout, RolloutTrigger.GateFailed, "3/9 failed; required < 2%", 2));
            Assert.True(machine.TryApply(rollout, RolloutTrigger.BeginRollback, "Restore original versions"));
            await db.SaveChangesAsync(timeout.Token);
        }
        var progress = await http.GetFromJsonAsync<RolloutProgress>($"/api/rollouts/{created.Id.Value}", timeout.Token);
        Assert.NotNull(progress);
        Assert.Equal(RolloutStatus.RollingBack, progress.Summary.Status);
        Assert.Equal("3/9 failed; required < 2%", progress.HaltReason);
        Assert.Equal("Restore original versions", progress.StateReason);
        Assert.Equal(7, progress.Waves.Sum(wave => wave.Installed));
        Assert.Equal(3, progress.Waves.Sum(wave => wave.RolledBack));
        Assert.Equal(1, progress.Waves.Sum(wave => wave.UnresolvedInstalls));
        Assert.Equal(3, progress.Waves[1].Failed);
        Assert.Equal(6, progress.Transitions.Count);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            var late = await db.WaveTargets.SingleAsync(target => target.InstallCommandId == expiredId, timeout.Token);
            late.InstallOutcome = CommandOutcome.Succeeded;
            await db.SaveChangesAsync(timeout.Token);
        }
        var refreshed = await http.GetFromJsonAsync<RolloutProgress>($"/api/rollouts/{created.Id.Value}", timeout.Token);
        Assert.NotNull(refreshed);
        Assert.Equal(8, refreshed.Waves.Sum(wave => wave.Installed));
        Assert.Equal(0, refreshed.Waves.Sum(wave => wave.UnresolvedInstalls));
        Assert.Equal(2, refreshed.Waves[1].Failed);
        Assert.Equal(3, refreshed.Waves[1].GateFailures);
        Assert.Equal(progress.HaltReason, refreshed.HaltReason);
        var recent = await http.GetFromJsonAsync<RolloutSummary[]>("/api/rollouts", timeout.Token);
        Assert.Equal(RolloutStatus.RollingBack, Assert.Single(recent!).Status);
        using var missing = await http.GetAsync($"/api/rollouts/{Guid.NewGuid()}", timeout.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var document = await http.GetFromJsonAsync<JsonDocument>("/openapi/v1.json", timeout.Token);
        Assert.Equal("uuid", document!.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("RolloutId").GetProperty("format").GetString());
        await app.StopAsync(timeout.Token);
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    }
}
