using FleetPulse.Contracts.Ids;
using FleetPulse.Gateway.Devices;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace FleetPulse.IntegrationTests;

public sealed class DevicePresenceTests
{
    [Fact]
    public async Task ReplacedConnectionSurvivesOldDisconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var container = new RedisBuilder("redis:7.4-alpine").Build();
        await container.StartAsync(timeout.Token);

        await using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var presence = new RedisDevicePresence(redis);
        var deviceId = DeviceId.New();

        await presence.ConnectAsync(deviceId, "old", timeout.Token);
        Assert.Equal("old", await presence.GetConnectionIdAsync(deviceId, timeout.Token));

        await presence.ConnectAsync(deviceId, "new", timeout.Token);
        await presence.DisconnectAsync(deviceId, "old", timeout.Token);
        Assert.Equal("new", await presence.GetConnectionIdAsync(deviceId, timeout.Token));

        await presence.DisconnectAsync(deviceId, "new", timeout.Token);
        Assert.Null(await presence.GetConnectionIdAsync(deviceId, timeout.Token));
    }
}
