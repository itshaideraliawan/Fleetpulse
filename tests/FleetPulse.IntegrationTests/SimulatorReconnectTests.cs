using System.Net;
using System.Net.Sockets;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Gateway.Devices;
using FleetPulse.Simulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FleetPulse.IntegrationTests;

public sealed class SimulatorReconnectTests
{
    [Fact]
    public async Task ReconnectsAndResumesMessagesAfterGatewayRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var port = GetFreePort();
        var gateway = new Uri($"http://127.0.0.1:{port}");
        var simulator = new SimulatorRunner(new SimulatorOptions(gateway, 1, 1));

        await using var firstGateway = CreateGateway(port, out var firstBuffer);
        await firstGateway.StartAsync(timeout.Token);
        var simulatorTask = simulator.RunAsync(timeout.Token);

        try
        {
            await AssertMessagesAsync(firstBuffer, timeout.Token);
            await firstGateway.StopAsync(timeout.Token);

            await using var secondGateway = CreateGateway(port, out var secondBuffer);
            await secondGateway.StartAsync(timeout.Token);
            await AssertMessagesAsync(secondBuffer, timeout.Token);
            await secondGateway.StopAsync(timeout.Token);
        }
        finally
        {
            timeout.Cancel();
            await simulatorTask;
        }
    }

    private static WebApplication CreateGateway(int port, out DeviceMessageBuffer buffer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<IDevicePresence, InMemoryPresence>();
        builder.Services.AddSingleton<IDeviceCommandDelivery, NoCommands>();
        builder.Services.AddSingleton<DeviceMessageBuffer>();
        var app = builder.Build();
        app.MapHub<DeviceHub>(DeviceHub.Route);
        buffer = app.Services.GetRequiredService<DeviceMessageBuffer>();
        return app;
    }

    private static async Task AssertMessagesAsync(DeviceMessageBuffer buffer, CancellationToken cancellationToken)
    {
        var first = await buffer.Reader.ReadAsync(cancellationToken);
        var second = await buffer.Reader.ReadAsync(cancellationToken);
        Assert.IsType<QueuedDeviceMessage.HeartbeatItem>(first);
        Assert.IsType<QueuedDeviceMessage.TelemetryItem>(second);
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
        public Task ConnectAsync(DeviceId deviceId, string connectionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DisconnectAsync(DeviceId deviceId, string connectionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> GetConnectionIdAsync(DeviceId deviceId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    private sealed class NoCommands : IDeviceCommandDelivery
    {
        public Task DeliverAsync(DeliverCommand delivery, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeliverPendingAsync(DeviceId deviceId, string connectionId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
