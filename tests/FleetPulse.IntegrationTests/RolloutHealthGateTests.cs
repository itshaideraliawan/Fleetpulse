using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Devices;
using FleetPulse.FleetApi.Rollouts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Wolverine;

namespace FleetPulse.IntegrationTests;

public sealed class RolloutHealthGateTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task FailedWaveRollsBackAllUpdatedDevicesIncludingLateResultsAndPreservesRetries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = timeout.Token;
        await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await using var rabbit = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
        await Task.WhenAll(sql.StartAsync(ct), rabbit.StartAsync(ct));
        var clock = new TestClock();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:rabbitmq"] = rabbit.GetConnectionString()
        });
        builder.Services.AddDbContext<FleetDbContext>(options => options.UseSqlServer(sql.GetConnectionString()));
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton<RolloutStateMachine>();
        builder.Services.AddScoped<RolloutOrchestrator>();
        builder.UseWolverine(options =>
        {
            FleetMessaging.Configure(options);
            FleetMessaging.ConfigureOutbox(options, sql.GetConnectionString());
        });
        using var host = builder.Build();
        var releaseId = ReleaseId.New();
        var originals = new Dictionary<DeviceId, string>();
        RolloutId id;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            await db.Database.MigrateAsync(ct);
            var site = new Site { Id = 1, Name = "Test", Region = new Region { Id = 1, Name = "North" } };
            for (var index = 0; index < 100; index++)
            {
                var deviceId = DeviceId.New();
                var version = index % 2 == 0 ? "1.0.0" : "1.1.0";
                originals.Add(deviceId, version);
                db.Devices.Add(new Device { Id = deviceId, SerialNumber = $"FP-{index:00000}",
                    Model = "Kiosk-A", FirmwareVersion = version, Site = site });
            }
            db.Releases.Add(new Release { Id = releaseId, FirmwareVersion = "2.4.0", CreatedAtUtc = clock.UtcNow });
            await db.SaveChangesAsync(ct);
            id = (await new RolloutPlanner(db, clock).CreateAsync(releaseId, originals.Keys.ToArray(), ct)).Id;
        }
        await host.StartAsync(ct);
        await using var connection = await new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) }.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await AdvanceAsync(host, id, ct);
        var canary = Assert.Single(await ReadAsync(channel, 1, ct));
        Assert.Equal(CommandKind.InstallFirmware, canary.Kind);
        Assert.Equal(id, canary.RolloutId);
        Assert.Equal(releaseId, canary.ReleaseId);
        Assert.Equal("2.4.0", canary.FirmwareVersion);
        await ResultAsync(host, clock, canary, CommandOutcome.Succeeded, ct);
        await ResultAsync(host, clock, canary, CommandOutcome.Failed, ct);
        await AdvanceAsync(host, id, ct);
        var secondWave = await ReadAsync(channel, 9, ct);
        foreach (var command in secondWave.Take(8)) await ResultAsync(host, clock, command, CommandOutcome.Succeeded, ct);
        var uncertain = secondWave[8];
        clock.UtcNow += RolloutOrchestrator.CommandLifetime + TimeSpan.FromSeconds(1);
        await new CommandRetryWorker(host.Services.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<CommandRetryWorker>.Instance).ScanAsync(ct);
        await AdvanceAsync(host, id, ct);
        var rollbacks = await ReadAsync(channel, 9, ct);
        Assert.Contains(rollbacks, command => command.Header.DeviceId == canary.Header.DeviceId);
        Assert.All(rollbacks, command =>
        {
            Assert.Equal(CommandKind.RollbackFirmware, command.Kind);
            Assert.Equal(originals[command.Header.DeviceId], command.FirmwareVersion);
            Assert.Equal(id, command.RolloutId);
            Assert.NotNull(command.WaveId);
        });
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            var rollout = await db.Rollouts.Include(item => item.Waves).Include(item => item.Transitions)
                .AsSplitQuery().SingleAsync(item => item.Id == id, ct);
            Assert.Equal(RolloutStatus.RollingBack, rollout.Status);
            var failed = rollout.Waves.Single(wave => wave.Number == 2);
            Assert.Equal(1, failed.GateFailures);
            Assert.Equal(9, failed.GateTotal);
            Assert.Contains(rollout.Transitions, transition => transition.ToStatus == RolloutStatus.Halted && transition.Reason.Contains("1/9", StringComparison.Ordinal));
            Assert.Equal(10, await db.CommandTargets.CountAsync(target => target.Command.Kind == CommandKind.InstallFirmware, ct));
        }

        clock.UtcNow += CommandTargetStateMachine.RetryInterval;
        await new CommandRetryWorker(host.Services.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<CommandRetryWorker>.Instance).ScanAsync(ct);
        var retried = await ReadAsync(channel, 9, ct);
        foreach (var retry in retried)
        {
            var original = rollbacks.Single(command => command.CommandId == retry.CommandId);
            Assert.Equal(original with { Header = retry.Header }, retry);
        }
        foreach (var command in rollbacks) await ResultAsync(host, clock, command, CommandOutcome.Succeeded, ct);
        await AdvanceAsync(host, id, ct);
        await AssertStateAsync(host, id, RolloutStatus.RollingBack, ct);

        await ResultAsync(host, clock, uncertain, CommandOutcome.Succeeded, ct);
        await AdvanceAsync(host, id, ct);
        var lateRollback = Assert.Single(await ReadAsync(channel, 1, ct));
        Assert.Equal(uncertain.Header.DeviceId, lateRollback.Header.DeviceId);
        Assert.Equal(originals[uncertain.Header.DeviceId], lateRollback.FirmwareVersion);
        await ResultAsync(host, clock, lateRollback, CommandOutcome.Succeeded, ct);
        await AdvanceAsync(host, id, ct);
        await AdvanceAsync(host, id, ct);
        await AssertStateAsync(host, id, RolloutStatus.RolledBack, ct);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            Assert.Equal(20, await db.CommandTargets.CountAsync(ct));
            var devices = await db.Devices.ToArrayAsync(ct);
            Assert.All(devices, device => Assert.Equal(originals[device.Id], device.FirmwareVersion));
        }

        // A healthy two-device cohort skips its empty middle waves and completes.
        RolloutId healthyId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            healthyId = (await new RolloutPlanner(db, clock).CreateAsync(releaseId, originals.Keys.Take(2).ToArray(), ct)).Id;
        }
        await AdvanceAsync(host, healthyId, ct);
        var healthyFirst = Assert.Single(await ReadAsync(channel, 1, ct));
        await ResultAsync(host, clock, healthyFirst, CommandOutcome.Succeeded, ct);
        await AdvanceAsync(host, healthyId, ct);
        var healthyLast = Assert.Single(await ReadAsync(channel, 1, ct));
        await ResultAsync(host, clock, healthyLast, CommandOutcome.Succeeded, ct);
        await AdvanceAsync(host, healthyId, ct);
        await AssertStateAsync(host, healthyId, RolloutStatus.Completed, ct);

        RolloutId unsuccessfulId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            unsuccessfulId = (await new RolloutPlanner(db, clock).CreateAsync(releaseId, originals.Keys.Skip(2).Take(2).ToArray(), ct)).Id;
        }
        await AdvanceAsync(host, unsuccessfulId, ct);
        var installed = Assert.Single(await ReadAsync(channel, 1, ct));
        await ResultAsync(host, clock, installed, CommandOutcome.Succeeded, ct);
        await AdvanceAsync(host, unsuccessfulId, ct);
        await ResultAsync(host, clock, Assert.Single(await ReadAsync(channel, 1, ct)), CommandOutcome.Failed, ct);
        await AdvanceAsync(host, unsuccessfulId, ct);
        var failedRollback = Assert.Single(await ReadAsync(channel, 1, ct));
        await ResultAsync(host, clock, failedRollback, CommandOutcome.Failed, ct);
        await AdvanceAsync(host, unsuccessfulId, ct);
        await ResultAsync(host, clock, failedRollback, CommandOutcome.Succeeded, ct);
        await AdvanceAsync(host, unsuccessfulId, ct);
        await AssertStateAsync(host, unsuccessfulId, RolloutStatus.RollingBack, ct);
        await host.StopAsync(ct);
    }

    private static async Task AdvanceAsync(IHost host, RolloutId id, CancellationToken cancellationToken)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RolloutOrchestrator>().AdvanceAsync(id, cancellationToken);
    }

    private static async Task ResultAsync(IHost host, IClock clock, DeviceCommand command,
        CommandOutcome outcome, CancellationToken cancellationToken)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await CommandReportHandler.HandleAsync(new CommandCompleted(new CommandResult(
            command.Header with { SentAtUtc = clock.UtcNow }, command.CommandId, outcome, "Device result")),
            scope.ServiceProvider.GetRequiredService<FleetDbContext>(), clock, cancellationToken);
    }

    private static async Task AssertStateAsync(IHost host, RolloutId id, RolloutStatus expected, CancellationToken cancellationToken)
    {
        await using var scope = host.Services.CreateAsyncScope();
        Assert.Equal(expected, (await scope.ServiceProvider.GetRequiredService<FleetDbContext>()
            .Rollouts.SingleAsync(item => item.Id == id, cancellationToken)).Status);
    }

    private static async Task<List<DeviceCommand>> ReadAsync(IChannel channel, int count, CancellationToken cancellationToken)
    {
        var commands = new Dictionary<CommandId, DeviceCommand>();
        while (commands.Count < count)
        {
            var delivery = await channel.BasicGetAsync(FleetMessaging.CommandQueue, autoAck: true, cancellationToken);
            if (delivery is null) { await Task.Delay(100, cancellationToken); continue; }
            var message = JsonSerializer.Deserialize<DeliverCommand>(delivery.Body.Span, Json)!;
            commands[message.Command.CommandId] = message.Command;
        }
        return commands.Values.ToList();
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    }
}
