using StackExchange.Redis;
using Testcontainers.Redis;

namespace FleetPulse.IntegrationTests;

public sealed class RedisContainerTests
{
    [Fact]
    public async Task RedisPersistsAndReadsValue()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var container = new RedisBuilder("redis:7.4-alpine").Build();
        await container.StartAsync(timeout.Token);

        await using var connection = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var database = connection.GetDatabase();
        var key = $"fleetpulse:test:{Guid.NewGuid():N}";

        Assert.True(await database.StringSetAsync(key, "online"));
        Assert.Equal("online", (string?)await database.StringGetAsync(key));
    }
}
