using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Gateway.Devices;
using FleetPulse.Simulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using Wolverine;

namespace FleetPulse.IntegrationTests;

public sealed class SimulatorCommandReportingTests
{
    [Fact]
    public async Task SimulatorAcknowledgesAndReportsOneStoredResultForDuplicateCommand()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var rabbitMq = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
        await rabbitMq.StartAsync(timeout.Token);

        var port = GetFreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:rabbitmq"] = rabbitMq.GetConnectionString()
        });
        builder.UseWolverine(GatewayMessaging.Configure);
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<DeviceMessageBuffer>();
        builder.Services.AddSingleton<IDevicePresence, InMemoryPresence>();
        builder.Services.AddSingleton<IDeviceCommandDelivery, NoCommands>();
        builder.Services.AddSingleton<ICommandReportSink, WolverineCommandReportSink>();
        await using var gateway = builder.Build();
        gateway.MapHub<DeviceHub>(DeviceHub.Route);
        await gateway.StartAsync(timeout.Token);

        var device = VirtualDevice.Create(1);
        var simulator = new SimulatorRunner(new SimulatorOptions(
            new Uri($"http://127.0.0.1:{port}"), 1, 1, CommandFailureRate: 1));
        using var simulatorStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var simulatorTask = simulator.RunAsync(simulatorStop.Token);

        try
        {
            var presence = gateway.Services.GetRequiredService<IDevicePresence>();
            string? connectionId;
            while ((connectionId = await presence.GetConnectionIdAsync(device.Id,
                       timeout.Token)) is null)
            {
                await Task.Delay(100, timeout.Token);
            }

            var command = new DeviceCommand(
                new DeviceMessageHeader(DeviceProtocolVersion.Current, device.Id,
                    DateTimeOffset.UtcNow), CommandId.New(), CommandKind.RunDiagnostics,
                DateTimeOffset.UtcNow.AddMinutes(5), null, null, null, null);
            var client = gateway.Services.GetRequiredService<IHubContext<DeviceHub>>()
                .Clients.Client(connectionId);
            await client.SendAsync("ReceiveCommand", command, timeout.Token);
            await client.SendAsync("ReceiveCommand", command, timeout.Token);

            var factory = new ConnectionFactory { Uri = new Uri(rabbitMq.GetConnectionString()) };
            await using var connection = await factory.CreateConnectionAsync(timeout.Token);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
            var acknowledgements = new List<Guid>();
            var results = new List<(Guid CommandId, int Outcome, string Detail, string SentAt)>();
            while (acknowledgements.Count < 2 || results.Count < 2)
            {
                if (acknowledgements.Count < 2 && await channel.BasicGetAsync(
                        GatewayMessaging.CommandAckQueue, true, timeout.Token) is { } ack)
                {
                    using var document = JsonDocument.Parse(ack.Body);
                    acknowledgements.Add(document.RootElement.GetProperty("message")
                        .GetProperty("commandId").GetGuid());
                }

                if (results.Count < 2 && await channel.BasicGetAsync(
                        GatewayMessaging.CommandResultQueue, true, timeout.Token) is { } result)
                {
                    using var document = JsonDocument.Parse(result.Body);
                    var message = document.RootElement.GetProperty("message");
                    results.Add((message.GetProperty("commandId").GetGuid(),
                        message.GetProperty("outcome").GetInt32(),
                        message.GetProperty("detail").GetString()!,
                        message.GetProperty("header").GetProperty("sentAtUtc").GetString()!));
                }

                await Task.Delay(100, timeout.Token);
            }

            Assert.All(acknowledgements, id => Assert.Equal(command.CommandId.Value, id));
            Assert.All(results, result =>
            {
                Assert.Equal(command.CommandId.Value, result.CommandId);
                Assert.Equal((int)CommandOutcome.Failed, result.Outcome);
                Assert.Equal("Diagnostics failed.", result.Detail);
            });
            Assert.Equal(results[0], results[1]);
        }
        finally
        {
            simulatorStop.Cancel();
            await simulatorTask;
            await gateway.StopAsync(CancellationToken.None);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class InMemoryPresence : IDevicePresence
    {
        private readonly ConcurrentDictionary<DeviceId, string> _connections = new();

        public Task ConnectAsync(DeviceId deviceId, string connectionId,
            CancellationToken cancellationToken)
        {
            _connections[deviceId] = connectionId;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(DeviceId deviceId, string connectionId,
            CancellationToken cancellationToken)
        {
            _connections.TryRemove(new KeyValuePair<DeviceId, string>(deviceId, connectionId));
            return Task.CompletedTask;
        }

        public Task<string?> GetConnectionIdAsync(DeviceId deviceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_connections.GetValueOrDefault(deviceId));
    }

    private sealed class NoCommands : IDeviceCommandDelivery
    {
        public Task DeliverAsync(DeliverCommand delivery, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeliverPendingAsync(DeviceId deviceId, string connectionId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
