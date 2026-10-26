using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace FleetPulse.IntegrationTests;

public sealed class FleetTelemetryStateTests
{
    [Fact]
    public async Task TracksHealthTelemetryAndOfflineTransitionsWithoutDoubleCounting()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var container = new RedisBuilder("redis:7.4-alpine").Build();
        await container.StartAsync(timeout.Token);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var database = redis.GetDatabase();
        var clock = new TestClock { UtcNow = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) };
        var state = new RedisFleetState(redis, clock);
        var deviceId = DeviceId.New();

        var first = Header(deviceId, clock.UtcNow);
        var heartbeatBatch = new DeviceHeartbeatBatch([new Heartbeat(first, DeviceHealth.Healthy)]);
        await FleetTelemetryHandler.HandleAsync(heartbeatBatch, state, timeout.Token);
        await FleetTelemetryHandler.HandleAsync(heartbeatBatch, state, timeout.Token);
        Assert.Equal("1", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "online"));
        Assert.Equal("1", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "healthy"));

        clock.UtcNow = clock.UtcNow.AddSeconds(10);
        var later = Header(deviceId, clock.UtcNow);
        var telemetryBatch = new DeviceTelemetryBatch([new Telemetry(later, DeviceHealth.Degraded, 63, 72, 61)]);
        await FleetTelemetryHandler.HandleAsync(telemetryBatch, state, timeout.Token);
        await state.RecordAsync(new Heartbeat(first, DeviceHealth.Healthy), timeout.Token);
        Assert.Equal("0", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "healthy"));
        Assert.Equal("1", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "degraded"));
        Assert.Equal("63", (string?)await database.HashGetAsync(RedisFleetState.DeviceKey(deviceId), "cpuPercent"));

        clock.UtcNow = clock.UtcNow.AddSeconds(44);
        Assert.Equal(0, await state.MarkExpiredAsync(timeout.Token));
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        Assert.Equal(1, await state.MarkExpiredAsync(timeout.Token));
        Assert.Equal("0", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "online"));
        Assert.Equal("0", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "degraded"));
        Assert.Equal("0", (string?)await database.HashGetAsync(RedisFleetState.DeviceKey(deviceId), "online"));

        await state.RecordAsync(new Telemetry(later, DeviceHealth.Degraded, 63, 72, 61), timeout.Token);
        await state.RecordAsync(new Heartbeat(first, DeviceHealth.Healthy), timeout.Token);
        Assert.Equal("0", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "online"));

        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        await state.RecordAsync(new Heartbeat(Header(deviceId, clock.UtcNow), DeviceHealth.Unhealthy), timeout.Token);
        Assert.Equal("1", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "online"));
        Assert.Equal("1", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "unhealthy"));
        Assert.Equal("0", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "degraded"));
    }

    private static DeviceMessageHeader Header(DeviceId deviceId, DateTimeOffset sentAt) =>
        new(DeviceProtocolVersion.Current, deviceId, sentAt);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
