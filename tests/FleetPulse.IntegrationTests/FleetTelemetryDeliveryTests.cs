using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using FleetPulse.Gateway.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Wolverine;

namespace FleetPulse.IntegrationTests;

public sealed class FleetTelemetryDeliveryTests
{
    [Fact]
    public async Task ConsumesGatewayBatchFromRabbitMqIntoRedis()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var rabbitMq = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
        await using var redisContainer = new RedisBuilder("redis:7.4-alpine").Build();
        await Task.WhenAll(rabbitMq.StartAsync(timeout.Token), redisContainer.StartAsync(timeout.Token));
        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisContainer.GetConnectionString());

        var consumerBuilder = Host.CreateApplicationBuilder();
        consumerBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:rabbitmq"] = rabbitMq.GetConnectionString()
        });
        consumerBuilder.UseWolverine(FleetMessaging.Configure);
        consumerBuilder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        consumerBuilder.Services.AddSingleton<IClock, SystemClock>();
        consumerBuilder.Services.AddSingleton<RedisFleetState>();
        using var consumer = consumerBuilder.Build();
        await consumer.StartAsync(timeout.Token);

        var publisherBuilder = Host.CreateApplicationBuilder();
        publisherBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:rabbitmq"] = rabbitMq.GetConnectionString()
        });
        publisherBuilder.UseWolverine(GatewayMessaging.Configure);
        using var publisher = publisherBuilder.Build();
        await publisher.StartAsync(timeout.Token);

        var deviceId = DeviceId.New();
        var message = new Telemetry(
            new DeviceMessageHeader(DeviceProtocolVersion.Current, deviceId, DateTimeOffset.UtcNow),
            DeviceHealth.Healthy, 20, 30, 40);
        await publisher.Services.GetRequiredService<IMessageBus>()
            .PublishAsync(new DeviceTelemetryBatch([message]));

        var database = redis.GetDatabase();
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if ((string?)await database.HashGetAsync(RedisFleetState.DeviceKey(deviceId), "online") == "1")
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }

        Assert.Equal("1", (string?)await database.HashGetAsync(RedisFleetState.CountsKey, "online"));
        Assert.Equal("20", (string?)await database.HashGetAsync(RedisFleetState.DeviceKey(deviceId), "cpuPercent"));

        await publisher.StopAsync(timeout.Token);
        await consumer.StopAsync(timeout.Token);
    }
}
