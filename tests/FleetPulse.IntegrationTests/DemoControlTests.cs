using System.Net;
using System.Net.Http.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Demo;
using FleetPulse.FleetApi.Devices;
using FleetPulse.FleetApi.Rollouts;
using FleetPulse.Gateway.Devices;
using FleetPulse.Simulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Wolverine;

namespace FleetPulse.IntegrationTests;

public sealed class DemoControlTests
{
    [Fact]
    public async Task OutboxControlsReachDevicesDropSessionsAndResetWithoutResurrectingOldState()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = timeout.Token;
        await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await using var rabbit = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
        await using var cache = new RedisBuilder("redis:7.4-alpine").Build();
        await Task.WhenAll(sql.StartAsync(ct), rabbit.StartAsync(ct), cache.StartAsync(ct));
        await using var redis = await ConnectionMultiplexer.ConnectAsync(cache.GetConnectionString());
        var clock = new TestClock();
        var apiBuilder = WebApplication.CreateBuilder();
        apiBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        apiBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:rabbitmq"] = rabbit.GetConnectionString() });
        apiBuilder.Services.AddDbContext<FleetDbContext>(options => options.UseSqlServer(sql.GetConnectionString()));
        apiBuilder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        apiBuilder.Services.AddSingleton<IClock>(clock);
        apiBuilder.Services.AddScoped<DemoQueries>();
        apiBuilder.Services.AddHostedService<DemoResetCompletionWorker>();
        apiBuilder.UseWolverine(options => { FleetMessaging.Configure(options); FleetMessaging.ConfigureOutbox(options, sql.GetConnectionString()); });
        await using var api = apiBuilder.Build();
        api.MapDemoEndpoints();
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            await db.Database.MigrateAsync(ct);
            var site = new Site { Id = 1, Name = "Demo", Region = new Region { Id = 1, Name = "North" } };
            for (var index = 1; index <= 2; index++)
            {
                var device = VirtualDevice.Create(index);
                db.Devices.Add(new Device { Id = device.Id, SerialNumber = $"FP-{index:00000}", Model = device.Model, FirmwareVersion = "2.4.0", Site = site });
            }
            await db.SaveChangesAsync(ct);
            await DemoControlHandler.HandleAsync(new DemoControlChanged((await db.DemoControls.SingleAsync(ct)).Snapshot()), db, redis, ct);
        }
        await api.StartAsync(ct);
        var gatewayBuilder = WebApplication.CreateBuilder();
        gatewayBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        gatewayBuilder.Services.AddDeviceAdmissionControl(gatewayBuilder.Configuration);
        gatewayBuilder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        gatewayBuilder.Services.AddSingleton<IClock>(clock);
        gatewayBuilder.Services.AddSingleton<IDevicePresence, RedisDevicePresence>();
        gatewayBuilder.Services.AddSingleton<PendingDeviceCommands>();
        gatewayBuilder.Services.AddSingleton<IDeviceCommandDelivery, GatewayCommandDelivery>();
        var reports = new NoReports();
        gatewayBuilder.Services.AddSingleton<ICommandReportSink>(reports);
        gatewayBuilder.Services.AddSingleton<DeviceMessageBuffer>();
        gatewayBuilder.Services.AddSingleton<DeviceConnectionRegistry>();
        gatewayBuilder.Services.AddSingleton<GatewayDemoControl>();
        gatewayBuilder.Services.AddHostedService(provider => provider.GetRequiredService<GatewayDemoControl>());
        gatewayBuilder.Services.AddSignalR();
        await using var gateway = gatewayBuilder.Build();
        gateway.UseDeviceAdmissionControl();
        gateway.MapHub<DeviceHub>(DeviceHub.Route, options => options.Transports = HttpTransportType.WebSockets);
        await redis.GetDatabase().KeyDeleteAsync([DemoControlKeys.Settings, DemoControlKeys.Revision]);
        await gateway.StartAsync(ct);
        using var simulation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var simulator = new SimulatorRunner(new SimulatorOptions(new Uri(Address(gateway)), 2, 1));
        var simulationTask = simulator.RunAsync(simulation.Token);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(Address(api)) };
            var startupRegistry = gateway.Services.GetRequiredService<DeviceConnectionRegistry>();
            while (startupRegistry.Count != 2) await Task.Delay(10, ct);
            var startupDevice = VirtualDevice.Create(1);
            var startupCommand = new DeviceCommand(new DeviceMessageHeader(DeviceProtocolVersion.Current, startupDevice.Id, clock.UtcNow),
                CommandId.New(), CommandKind.RunDiagnostics, clock.UtcNow.AddMinutes(5), null, null, null, null);
            await gateway.Services.GetRequiredService<IDeviceCommandDelivery>().DeliverAsync(new DeliverCommand(startupCommand), ct);
            Assert.Empty(reports.Results);
            Assert.Single(await gateway.Services.GetRequiredService<PendingDeviceCommands>().GetAsync(startupDevice.Id, ct));
            await using (var scope = api.Services.CreateAsyncScope())
                await DemoControlHandler.HandleAsync(new DemoControlChanged(new DemoControlSnapshot(0, true, null, null, 0, 0, null)),
                    scope.ServiceProvider.GetRequiredService<FleetDbContext>(), redis, ct);
            var initial = await WaitAsync(http, status => status.ConnectedDevices == 2 && Ready(status), ct);
            while (!reports.Results.Any(result => result.Message.CommandId == startupCommand.CommandId)) await Task.Delay(10, ct);
            Assert.Equal(0, initial.Desired.Revision);
            var request = new DemoRequest(Guid.NewGuid(), 0, DemoAction.Settings, false, "2.4.0", "Kiosk-B");
            using var invalid = await http.PostAsJsonAsync("/api/demo", request with { BadReleaseModel = "unknown" }, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            await PostAsync(http, request, ct);
            await PostAsync(http, request, ct);
            var configured = await WaitAsync(http, status => status.Desired.Revision == 1 && Ready(status), ct);
            Assert.False(gateway.Services.GetRequiredService<ConnectionAdmissionControl>().Enabled);
            Assert.Equal("2.4.0", configured.Desired.BadReleaseVersion);
            var badDevice = VirtualDevice.Create(2);
            var badCommand = new DeviceCommand(new DeviceMessageHeader(DeviceProtocolVersion.Current, badDevice.Id, clock.UtcNow),
                CommandId.New(), CommandKind.InstallFirmware, clock.UtcNow.AddMinutes(5), ReleaseId.New(), RolloutId.New(), WaveId.New(), "2.4.0");
            var delivery = gateway.Services.GetRequiredService<IDeviceCommandDelivery>();
            await delivery.DeliverAsync(new DeliverCommand(badCommand), ct);
            while (!reports.Results.Any(result => result.Message.CommandId == badCommand.CommandId)) await Task.Delay(10, ct);
            Assert.Equal(CommandOutcome.Failed, reports.Results.Single(result => result.Message.CommandId == badCommand.CommandId).Message.Outcome);
            var deviceConnection = await gateway.Services.GetRequiredService<IDevicePresence>().GetConnectionIdAsync(badDevice.Id, ct);
            await redis.GetDatabase().KeyDeleteAsync($"fleetpulse:demo:ready:{deviceConnection}");
            var diagnostic = badCommand with { CommandId = CommandId.New(), Kind = CommandKind.RunDiagnostics };
            await delivery.DeliverAsync(new DeliverCommand(diagnostic), ct);
            while (!reports.Results.Any(result => result.Message.CommandId == diagnostic.CommandId)) await Task.Delay(10, ct);
            Assert.Equal(CommandOutcome.Succeeded, reports.Results.Single(result => result.Message.CommandId == diagnostic.CommandId).Message.Outcome);
            using var stale = await http.PostAsJsonAsync("/api/demo", request with { RequestId = Guid.NewGuid() }, ct);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var registry = gateway.Services.GetRequiredService<DeviceConnectionRegistry>();
            var originalConnections = registry.ConnectionIds;
            await PostAsync(http, request with { RequestId = Guid.NewGuid(), ExpectedRevision = 1, Action = DemoAction.DropConnections }, ct);
            var recovered = await WaitAsync(http, status => status.Desired.Revision == 2 && Ready(status) &&
                status.ConnectedDevices == 2 && !registry.ConnectionIds.Intersect(originalConnections).Any(), ct);
            Assert.Equal(2, recovered.RecoveryTarget);
            Assert.NotNull(recovered.DropAtUtc);
            var connectionsAfterDrop = registry.ConnectionIds;
            await gateway.Services.GetRequiredService<GatewayDemoControl>().ApplyAsync(ct);
            Assert.Equal(connectionsAfterDrop.Order(), registry.ConnectionIds.Order());

            await using (var scope = api.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
                var release = new Release { Id = ReleaseId.New(), FirmwareVersion = "2.4.0", CreatedAtUtc = clock.UtcNow };
                db.Releases.Add(release);
                await db.SaveChangesAsync(ct);
                var ids = await db.Devices.Select(device => device.Id).ToArrayAsync(ct);
                var rollout = await new RolloutPlanner(db, clock).CreateAsync(release.Id, ids, ct);
                using var active = await http.PostAsJsonAsync("/api/demo", request with { RequestId = Guid.NewGuid(), ExpectedRevision = 2, Action = DemoAction.Reset }, ct);
                Assert.Equal(HttpStatusCode.Conflict, active.StatusCode);
                var machine = new RolloutStateMachine(clock);
                machine.TryApply(rollout, RolloutTrigger.Start, "Start");
                machine.TryApply(rollout, RolloutTrigger.WaveFinished, "Finished", 1);
                machine.TryApply(rollout, RolloutTrigger.GatePassed, "Passed", 1);
                machine.TryApply(rollout, RolloutTrigger.WaveFinished, "Finished", 4);
                machine.TryApply(rollout, RolloutTrigger.GatePassed, "Passed", 4);
                var command = new Command { Id = Guid.NewGuid(), Kind = CommandKind.InstallFirmware, CreatedAtUtc = clock.UtcNow, ExpiresAtUtc = clock.UtcNow.AddMinutes(5) };
                var target = new CommandTarget { Id = CommandId.New(), DeviceId = ids[0], Status = CommandTargetStatus.Succeeded, CreatedAtUtc = clock.UtcNow, LastSentAtUtc = clock.UtcNow };
                command.Targets.Add(target); db.Commands.Add(command);
                rollout.Waves.SelectMany(wave => wave.Targets).First(item => item.DeviceId == ids[0]).InstallCommand = target;
                await db.SaveChangesAsync(ct);
            }
            var hotState = new RedisFleetState(redis, clock);
            var oldHeader = new DeviceMessageHeader(DeviceProtocolVersion.Current, VirtualDevice.Create(1).Id, clock.UtcNow.AddSeconds(-1));
            await hotState.RecordAsync(new Heartbeat(oldHeader, DeviceHealth.Healthy), ct);
            await PostAsync(http, request with { RequestId = Guid.NewGuid(), ExpectedRevision = 2, Action = DemoAction.Reset }, ct);
            var reset = await WaitAsync(http, status => status.Desired.Revision == 3 && status.ConnectedDevices == 2 && Ready(status), ct);
            Assert.Null(reset.Desired.BadReleaseVersion);
            Assert.True(reset.Desired.AdmissionEnabled);
            Assert.Null(reset.DropAtUtc);
            await using (var scope = api.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
                Assert.All(await db.Devices.ToArrayAsync(ct), device => Assert.Equal("1.0.0", device.FirmwareVersion));
                Assert.Equal(0, await db.Rollouts.CountAsync(ct));
                Assert.Equal(0, await db.Commands.CountAsync(ct));
                Assert.Equal(0, await db.WaveTargets.CountAsync(ct));
                Assert.Equal(0, await db.RolloutTransitions.CountAsync(ct));
                await DemoControlHandler.HandleAsync(new DemoControlChanged(configured.Desired), db, redis, ct);
                await DemoControlHandler.HandleAsync(new DemoControlChanged(reset.Desired), db, redis, ct);
            }
            Assert.False(await redis.GetDatabase().KeyExistsAsync(RedisFleetState.CountsKey));
            await hotState.RecordAsync(new Heartbeat(oldHeader, DeviceHealth.Healthy), ct);
            Assert.False(await redis.GetDatabase().KeyExistsAsync(RedisFleetState.CountsKey));
            await hotState.RecordAsync(new Heartbeat(oldHeader with { SentAtUtc = clock.UtcNow.AddSeconds(1) }, DeviceHealth.Healthy), ct);
            Assert.Equal(1, (int)await redis.GetDatabase().HashGetAsync(RedisFleetState.CountsKey, "online"));
        }
        finally
        {
            simulation.Cancel(); await simulationTask;
            await gateway.StopAsync(CancellationToken.None);
            await api.StopAsync(CancellationToken.None);
        }
    }

    private static bool Ready(DemoStatus status) => !status.ResetPending && status.AppliedRevision == status.Desired.Revision && status.Gateways.Count > 0 &&
        status.Gateways.All(gateway => gateway.Revision == status.Desired.Revision && gateway.ConfiguredDevices == gateway.ConnectedDevices);
    private static string Address(WebApplication app) => app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static async Task<DemoStatus> WaitAsync(HttpClient http, Func<DemoStatus, bool> predicate, CancellationToken cancellationToken)
    {
        while (true)
        {
            var status = (await http.GetFromJsonAsync<DemoStatus>("/api/demo", cancellationToken))!;
            if (predicate(status)) return status;
            await Task.Delay(100, cancellationToken);
        }
    }
    private static async Task PostAsync(HttpClient http, DemoRequest request, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("/api/demo", request, cancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
    private sealed class TestClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class NoReports : ICommandReportSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<CommandCompleted> Results { get; } = new();
        public Task PublishAsync(CommandAcknowledged acknowledgement, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PublishAsync(CommandCompleted result, CancellationToken cancellationToken)
        {
            Results.Enqueue(result);
            return Task.CompletedTask;
        }
    }
}
