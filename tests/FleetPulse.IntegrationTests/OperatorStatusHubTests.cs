using System.Threading.Channels;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace FleetPulse.IntegrationTests;

public sealed class OperatorStatusHubTests
{
    [Fact]
    public async Task CoalescesStatusChangesAndSendsAtMostOncePerSecond()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var container = new RedisBuilder("redis:7.4-alpine").Build();
        await container.StartAsync(timeout.Token);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var clock = new TestClock { UtcNow = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) };
        var state = new RedisFleetState(redis, clock);
        await using var app = CreateApp(container.GetConnectionString());
        await using var secondApp = CreateApp(container.GetConnectionString());
        await app.StartAsync(timeout.Token);
        await secondApp.StartAsync(timeout.Token);
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var received = Channel.CreateUnbounded<OperatorStatusBatch>();
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{address}{OperatorHub.Route}", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            })
            .Build();
        connection.On<OperatorStatusBatch>(OperatorHub.StatusBatchMethod,
            batch => received.Writer.TryWrite(batch));
        await connection.StartAsync(timeout.Token);
        using var broadcaster = new StatusBroadcaster(redis,
            secondApp.Services.GetRequiredService<IHubContext<OperatorHub>>(), clock,
            NullLogger<StatusBroadcaster>.Instance);
        using var localBroadcaster = new StatusBroadcaster(redis,
            app.Services.GetRequiredService<IHubContext<OperatorHub>>(), clock,
            NullLogger<StatusBroadcaster>.Instance);
        await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token);

        var deviceId = DeviceId.New();
        await state.RecordAsync(Heartbeat(deviceId, DeviceHealth.Healthy, clock.UtcNow), timeout.Token);
        await state.RecordAsync(Heartbeat(deviceId, DeviceHealth.Healthy, clock.UtcNow), timeout.Token);
        await state.RecordAsync(new Telemetry(
            new DeviceMessageHeader(DeviceProtocolVersion.Current, deviceId, clock.UtcNow),
            DeviceHealth.Degraded, 65, 70, 60), timeout.Token);
        await broadcaster.FlushAsync(timeout.Token);
        var first = await received.Reader.ReadAsync(timeout.Token);
        Assert.Single(first.Changes);
        Assert.Equal(deviceId, first.Changes[0].DeviceId);
        Assert.Equal("degraded", first.Changes[0].Health);
        Assert.Equal(clock.UtcNow, first.Changes[0].LastMessageSentAt);
        Assert.Equal(2, first.MessagesSinceLastPush);
        Assert.Equal(2, first.MessagesPerSecond);

        clock.UtcNow = clock.UtcNow.AddMilliseconds(500);
        await state.RecordAsync(Heartbeat(deviceId, DeviceHealth.Healthy, clock.UtcNow), timeout.Token);
        await localBroadcaster.FlushAsync(timeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token);
        Assert.False(received.Reader.TryRead(out _));

        clock.UtcNow = clock.UtcNow.AddMilliseconds(500);
        await localBroadcaster.FlushAsync(timeout.Token);
        var second = await received.Reader.ReadAsync(timeout.Token);
        Assert.Single(second.Changes);
        Assert.Equal("healthy", second.Changes[0].Health);
        Assert.Equal(clock.UtcNow.AddMilliseconds(-500), second.Changes[0].LastMessageSentAt);
        Assert.Equal(1, second.MessagesSinceLastPush);
        Assert.False(received.Reader.TryRead(out _));

        clock.UtcNow = clock.UtcNow.AddSeconds(45);
        Assert.Equal(1, await state.MarkExpiredAsync(timeout.Token));
        await broadcaster.FlushAsync(timeout.Token);
        var offline = await received.Reader.ReadAsync(timeout.Token);
        Assert.False(Assert.Single(offline.Changes).Online);
        Assert.Equal(0, offline.MessagesSinceLastPush);

        await connection.StopAsync(timeout.Token);
        await secondApp.StopAsync(timeout.Token);
        await app.StopAsync(timeout.Token);
    }

    private static WebApplication CreateApp(string redisConnectionString)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR().AddStackExchangeRedis(redisConnectionString, options =>
            options.Configuration.ChannelPrefix = RedisChannel.Literal("fleetpulse:operator"));
        var app = builder.Build();
        app.MapHub<OperatorHub>(OperatorHub.Route);
        return app;
    }

    private static Heartbeat Heartbeat(DeviceId deviceId, DeviceHealth health, DateTimeOffset sentAt) =>
        new(new DeviceMessageHeader(DeviceProtocolVersion.Current, deviceId, sentAt), health);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
