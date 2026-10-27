using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Devices;
using FleetPulse.FleetApi.Rollouts;
using FleetPulse.Gateway.Devices;
using FleetPulse.Simulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Wolverine;

namespace FleetPulse.IntegrationTests;

public sealed class SimulatorFirmwareRolloutTests
{
    [Fact]
    public async Task ModelSpecificFailureHaltsWaveTwoAndRealDevicesConfirmRollback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = timeout.Token;
        await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await using var rabbit = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
        await using var redisContainer = new RedisBuilder("redis:7.4-alpine").Build();
        await Task.WhenAll(sql.StartAsync(ct), rabbit.StartAsync(ct), redisContainer.StartAsync(ct));
        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisContainer.GetConnectionString());
        var configuration = new Dictionary<string, string?> { ["ConnectionStrings:rabbitmq"] = rabbit.GetConnectionString() };

        var fleetBuilder = Host.CreateApplicationBuilder();
        fleetBuilder.Configuration.AddInMemoryCollection(configuration);
        fleetBuilder.Services.AddDbContext<FleetDbContext>(options => options.UseSqlServer(sql.GetConnectionString()));
        fleetBuilder.Services.AddSingleton<IClock, SystemClock>();
        fleetBuilder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        fleetBuilder.Services.AddSingleton<RedisFleetState>();
        fleetBuilder.Services.AddSingleton<RolloutStateMachine>();
        fleetBuilder.Services.AddScoped<RolloutOrchestrator>();
        fleetBuilder.UseWolverine(options =>
        {
            FleetMessaging.Configure(options);
            FleetMessaging.ConfigureOutbox(options, sql.GetConnectionString());
        });
        using var fleet = fleetBuilder.Build();
        var releaseId = ReleaseId.New();
        RolloutId rolloutId;
        await using (var scope = fleet.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            await db.Database.MigrateAsync(ct);
            var site = new Site { Id = 1, Name = "Test", Region = new Region { Id = 1, Name = "North" } };
            var devices = Enumerable.Range(1, 100).Select(VirtualDevice.Create).ToArray();
            foreach (var device in devices)
                db.Devices.Add(new Device { Id = device.Id, Model = device.Model, FirmwareVersion = device.FirmwareVersion,
                    SerialNumber = $"FP-{device.Sequence:00000}", Site = site });
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            db.Releases.Add(new Release { Id = releaseId, FirmwareVersion = "2.4.0", CreatedAtUtc = clock.UtcNow });
            await db.SaveChangesAsync(ct);
            rolloutId = (await new RolloutPlanner(db, clock).CreateAsync(releaseId, devices.Select(device => device.Id).ToArray(), ct)).Id;
        }
        await fleet.StartAsync(ct);

        var gatewayBuilder = WebApplication.CreateBuilder();
        gatewayBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        gatewayBuilder.Configuration.AddInMemoryCollection(configuration);
        gatewayBuilder.UseWolverine(GatewayMessaging.Configure);
        gatewayBuilder.Services.AddSingleton<IClock, SystemClock>();
        gatewayBuilder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        gatewayBuilder.Services.AddSingleton<IDevicePresence, RedisDevicePresence>();
        gatewayBuilder.Services.AddSingleton<PendingDeviceCommands>();
        gatewayBuilder.Services.AddSingleton<IDeviceCommandDelivery, GatewayCommandDelivery>();
        gatewayBuilder.Services.AddSingleton<ICommandReportSink, WolverineCommandReportSink>();
        gatewayBuilder.Services.AddSingleton<DeviceMessageBuffer>();
        gatewayBuilder.Services.AddSingleton<IDeviceBatchSink, WolverineBatchSink>();
        gatewayBuilder.Services.AddHostedService<DeviceMessagePublisher>();
        gatewayBuilder.Services.AddSignalR().AddStackExchangeRedis(redisContainer.GetConnectionString(), options =>
            options.Configuration.ChannelPrefix = RedisChannel.Literal("fleetpulse:gateway"));
        await using var gateway = gatewayBuilder.Build();
        gateway.MapHub<DeviceHub>(DeviceHub.Route);
        await gateway.StartAsync(ct);
        var address = gateway.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var simulator = new SimulatorRunner(new SimulatorOptions(new Uri(address), 100, 1,
            CommandFailureRate: 1, BadReleaseVersion: "2.4.0", BadReleaseModel: "Kiosk-B"));
        using var simulatorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var simulatorTask = simulator.RunAsync(simulatorStop.Token);
        try
        {
            while (true)
            {
                await using var scope = fleet.Services.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<RolloutOrchestrator>().AdvanceAsync(rolloutId, ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    await Task.Delay(250, ct);
                    continue;
                }
                var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
                var status = await db.Rollouts.AsNoTracking().Where(item => item.Id == rolloutId)
                    .Select(item => item.Status).SingleAsync(ct);
                if (status == RolloutStatus.RolledBack) break;
                await Task.Delay(250, ct);
            }

            await using var verification = fleet.Services.CreateAsyncScope();
            var database = verification.ServiceProvider.GetRequiredService<FleetDbContext>();
            var rollout = await database.Rollouts.Include(item => item.Waves).Include(item => item.Transitions)
                .AsSplitQuery().SingleAsync(item => item.Id == rolloutId, ct);
            Assert.Equal(0, rollout.Waves.Single(wave => wave.Number == 1).GateFailures);
            Assert.Equal(1, rollout.Waves.Single(wave => wave.Number == 1).GateTotal);
            Assert.Equal(3, rollout.Waves.Single(wave => wave.Number == 2).GateFailures);
            Assert.Equal(9, rollout.Waves.Single(wave => wave.Number == 2).GateTotal);
            Assert.All(rollout.Waves.Where(wave => wave.Number > 2), wave => Assert.Null(wave.GateCheckedAtUtc));
            Assert.Contains(rollout.Transitions, item => item.ToStatus == RolloutStatus.Halted && item.Reason.Contains("3/9", StringComparison.Ordinal));
            var installs = await database.CommandTargets.Where(target => target.Command.Kind == CommandKind.InstallFirmware)
                .ToArrayAsync(ct);
            Assert.Equal(10, installs.Length);
            Assert.Equal(3, installs.Count(target => target.Status == CommandTargetStatus.Failed));
            foreach (var failed in installs.Where(target => target.Status == CommandTargetStatus.Failed))
                Assert.Equal("Kiosk-B", (await database.Devices.SingleAsync(device => device.Id == failed.DeviceId, ct)).Model);
            var rollbacks = await database.CommandTargets.Where(target => target.Command.Kind == CommandKind.RollbackFirmware)
                .ToArrayAsync(ct);
            Assert.Equal(7, rollbacks.Length);
            Assert.All(rollbacks, target => Assert.Equal(CommandTargetStatus.Succeeded, target.Status));
            Assert.All(await database.Devices.ToArrayAsync(ct), device => Assert.Equal("1.0.0", device.FirmwareVersion));
        }
        finally
        {
            simulatorStop.Cancel();
            await simulatorTask;
            await gateway.StopAsync(CancellationToken.None);
            await fleet.StopAsync(CancellationToken.None);
        }
    }
}
