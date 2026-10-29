using System.Net;
using System.Net.Sockets;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.Gateway.Devices;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Wolverine;
using Wolverine.RabbitMQ;

namespace FleetPulse.IntegrationTests;

public sealed class GatewayCommandDeliveryTests
{
    [Fact]
    public async Task DeliversAcrossGatewayInstancesAndDrainsPendingOnReconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using var redisContainer = new RedisBuilder("redis:7.4-alpine").Build();
        await using var rabbitMq = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
        await Task.WhenAll(redisContainer.StartAsync(timeout.Token), rabbitMq.StartAsync(timeout.Token));

        var redisConnection = redisContainer.GetConnectionString();
        var rabbitConnection = rabbitMq.GetConnectionString();
        await using var consumer = CreateGateway(GetFreePort(), redisConnection, rabbitConnection);
        var receiverPort = GetFreePort();
        await using var receiver = CreateGateway(receiverPort, redisConnection, null);
        await consumer.StartAsync(timeout.Token);
        await receiver.StartAsync(timeout.Token);

        var publisherBuilder = Host.CreateApplicationBuilder();
        publisherBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:rabbitmq"] = rabbitConnection
        });
        publisherBuilder.UseWolverine(options =>
        {
            options.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
            options.UseRabbitMqUsingNamedConnection("rabbitmq").AutoProvision();
            options.PublishMessage<DeliverCommand>()
                .ToRabbitQueue(GatewayMessaging.CommandQueue).SendInline();
            options.PublishMessage<DeliverCommandBatch>()
                .ToRabbitQueue(GatewayMessaging.CommandQueue).SendInline();
        });
        using var publisher = publisherBuilder.Build();
        await publisher.StartAsync(timeout.Token);

        var deviceId = DeviceId.New();
        var live = CreateCommand(deviceId, DateTimeOffset.UtcNow.AddMinutes(5));
        var secondLive = CreateCommand(DeviceId.New(), DateTimeOffset.UtcNow.AddMinutes(5));
        await using (var connection = CreateConnection(receiverPort, deviceId, out var liveReceived))
        await using (var secondConnection = CreateConnection(receiverPort, secondLive.Header.DeviceId,
                         out var secondReceived))
        {
            await connection.StartAsync(timeout.Token);
            await secondConnection.StartAsync(timeout.Token);
            await publisher.Services.GetRequiredService<IMessageBus>()
                .PublishAsync(new DeliverCommandBatch([live, secondLive]));
            Assert.Equal(live.CommandId, (await liveReceived.Task.WaitAsync(timeout.Token)).CommandId);
            Assert.Equal(secondLive.CommandId, (await secondReceived.Task.WaitAsync(timeout.Token)).CommandId);
            await secondConnection.StopAsync(timeout.Token);
            await connection.StopAsync(timeout.Token);
        }

        var pending = receiver.Services.GetRequiredService<PendingDeviceCommands>();
        var presence = receiver.Services.GetRequiredService<IDevicePresence>();
        while (await presence.GetConnectionIdAsync(deviceId, timeout.Token) is not null)
        {
            await Task.Delay(100, timeout.Token);
        }

        var offline = CreateCommand(deviceId, DateTimeOffset.UtcNow.AddMinutes(5));
        await publisher.Services.GetRequiredService<IMessageBus>()
            .PublishAsync(new DeliverCommand(offline));
        while ((await pending.GetAsync(deviceId, timeout.Token)).Count == 0)
        {
            await Task.Delay(100, timeout.Token);
        }

        await using (var reconnected = CreateConnection(receiverPort, deviceId, out var replayed))
        {
            await reconnected.StartAsync(timeout.Token);
            Assert.Equal(offline.CommandId, (await replayed.Task.WaitAsync(timeout.Token)).CommandId);
            Assert.Empty(await pending.GetAsync(deviceId, timeout.Token));
            await reconnected.StopAsync(timeout.Token);
        }

        var expired = CreateCommand(deviceId, DateTimeOffset.UtcNow.AddSeconds(-1));
        await consumer.Services.GetRequiredService<IDeviceCommandDelivery>()
            .DeliverAsync(new DeliverCommand(expired), timeout.Token);
        Assert.Empty(await pending.GetAsync(deviceId, timeout.Token));

        await pending.SaveAsync(expired, timeout.Token);
        await using (var reconnected = CreateConnection(receiverPort, deviceId, out var expiredReceived))
        {
            await reconnected.StartAsync(timeout.Token);
            while ((await pending.GetAsync(deviceId, timeout.Token)).Count > 0)
            {
                await Task.Delay(100, timeout.Token);
            }

            Assert.Empty(await pending.GetAsync(deviceId, timeout.Token));
            Assert.False(expiredReceived.Task.IsCompleted);
            await reconnected.StopAsync(timeout.Token);
        }

        await publisher.StopAsync(timeout.Token);
        await receiver.StopAsync(timeout.Token);
        await consumer.StopAsync(timeout.Token);
    }

    private static WebApplication CreateGateway(int port, string redisConnection,
        string? rabbitConnection)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        if (rabbitConnection is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:rabbitmq"] = rabbitConnection
            });
            builder.UseWolverine(GatewayMessaging.Configure);
        }

        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnection));
        builder.Services.AddSingleton<IDevicePresence, RedisDevicePresence>();
        builder.Services.AddSingleton<PendingDeviceCommands>();
        builder.Services.AddSingleton<IDeviceCommandDelivery, GatewayCommandDelivery>();
        builder.Services.AddSingleton<ICommandReportSink, NoReports>();
        builder.Services.AddSingleton<DeviceMessageBuffer>();
        builder.Services.AddSignalR()
            .AddStackExchangeRedis(redisConnection, options =>
                options.Configuration.ChannelPrefix = RedisChannel.Literal("fleetpulse:gateway"));

        var app = builder.Build();
        app.MapHub<DeviceHub>(DeviceHub.Route);
        return app;
    }

    private static HubConnection CreateConnection(int port, DeviceId deviceId,
        out TaskCompletionSource<DeviceCommand> received)
    {
        var completion = new TaskCompletionSource<DeviceCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        received = completion;
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{port}{DeviceHub.Route}?deviceId={deviceId}")
            .Build();
        connection.On<DeviceCommand>(GatewayCommandDelivery.ReceiveMethod,
            command => completion.TrySetResult(command));
        return connection;
    }

    private static DeviceCommand CreateCommand(DeviceId deviceId, DateTimeOffset expiresAtUtc) =>
        new(new DeviceMessageHeader(DeviceProtocolVersion.Current, deviceId,
            DateTimeOffset.UtcNow), CommandId.New(), CommandKind.RunDiagnostics,
            expiresAtUtc, null, null, null, null);

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class NoReports : ICommandReportSink
    {
        public Task PublishAsync(CommandAcknowledged acknowledgement,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishAsync(CommandCompleted result,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
