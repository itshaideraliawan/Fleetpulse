using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Gateway.Devices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using Wolverine;

namespace FleetPulse.IntegrationTests;

public sealed class DeviceBatchDeliveryTests
{
    [Fact]
    public async Task PublishesBatchesToRabbitMqQueues()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var rabbitMq = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
        await rabbitMq.StartAsync(timeout.Token);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:rabbitmq"] = rabbitMq.GetConnectionString()
        });
        builder.UseWolverine(GatewayMessaging.Configure);
        builder.Services.AddSingleton<DeviceMessageBuffer>();
        builder.Services.AddSingleton<IDeviceBatchSink, WolverineBatchSink>();
        builder.Services.AddSingleton<DeviceMessagePublisher>(services => new DeviceMessagePublisher(
            services.GetRequiredService<DeviceMessageBuffer>(),
            services.GetRequiredService<IDeviceBatchSink>(),
            NullLogger<DeviceMessagePublisher>.Instance));

        using var host = builder.Build();
        await host.StartAsync(timeout.Token);

        var buffer = host.Services.GetRequiredService<DeviceMessageBuffer>();
        var header = new DeviceMessageHeader(1, DeviceId.New(), DateTimeOffset.UnixEpoch);
        for (var index = 0; index < DeviceMessagePublisher.MaxBatchSize + 1; index++)
        {
            Assert.True(buffer.TryWrite(new Heartbeat(header, DeviceHealth.Healthy)));
        }

        Assert.True(buffer.TryWrite(new Telemetry(header, DeviceHealth.Healthy, 1, 2, 3)));
        await host.Services.GetRequiredService<DeviceMessagePublisher>().FlushAsync(timeout.Token);

        var factory = new ConnectionFactory { Uri = new Uri(rabbitMq.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync(timeout.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);

        var heartbeatCounts = new List<int>();
        while (heartbeatCounts.Count < 2)
        {
            var delivery = await PollAsync(channel, GatewayMessaging.HeartbeatQueue, timeout.Token);
            heartbeatCounts.Add(ReadCount(delivery.Body));
        }

        var telemetryDelivery = await PollAsync(channel, GatewayMessaging.TelemetryQueue, timeout.Token);
        Assert.Equal([1, DeviceMessagePublisher.MaxBatchSize], heartbeatCounts.Order());
        Assert.Equal(1, ReadCount(telemetryDelivery.Body));

        await host.StopAsync(timeout.Token);
    }

    private static async Task<BasicGetResult> PollAsync(IChannel channel, string queue, CancellationToken cancellationToken)
    {
        while (true)
        {
            var delivery = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken);
            if (delivery is not null)
            {
                return delivery;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    private static int ReadCount(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("messages").GetArrayLength();
    }
}
