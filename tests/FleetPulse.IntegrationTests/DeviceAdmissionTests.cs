using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Gateway.Devices;
using FleetPulse.Simulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace FleetPulse.IntegrationTests;

public sealed class DeviceAdmissionTests
{
    [Fact]
    public async Task RejectsBeforePresenceAndSupportsRefillAndLiveSwitchWithoutDroppingConnections()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var container = new RedisBuilder("redis:7.4-alpine").Build();
        await container.StartAsync(timeout.Token);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdmissionControl:Enabled"] = "true",
            ["AdmissionControl:PermitsPerSecond"] = "1",
            ["AdmissionControl:BurstCapacity"] = "1"
        });
        builder.Services.AddDeviceAdmissionControl(builder.Configuration);
        var time = new ManualTime();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        builder.Services.AddSingleton<IDevicePresence, RedisDevicePresence>();
        builder.Services.AddSingleton<IDeviceCommandDelivery, NoCommands>();
        builder.Services.AddSingleton<ICommandReportSink, NoReports>();
        builder.Services.AddSingleton<DeviceMessageBuffer>();
        builder.Services.AddSignalR();
        await using var app = builder.Build();
        app.UseDeviceAdmissionControl();
        app.MapHub<DeviceHub>(DeviceHub.Route, options => options.Transports = HttpTransportType.WebSockets);
        app.MapGet("/health", () => "Healthy");
        await app.StartAsync(timeout.Token);
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        var firstId = DeviceId.New();
        var rejectedId = DeviceId.New();
        var hint = TimeSpan.Zero;
        await using var first = CreateConnection(address, firstId, _ => { });
        await first.StartAsync(timeout.Token);
        var presence = app.Services.GetRequiredService<IDevicePresence>();
        await WaitForPresenceAsync(presence, firstId, timeout.Token);
        await using var rejected = CreateConnection(address, rejectedId, value => hint = value);
        var rejection = await Assert.ThrowsAsync<AggregateException>(() => rejected.StartAsync(timeout.Token));
        Assert.IsType<WebSocketException>(Assert.Single(rejection.InnerExceptions).InnerException);
        Assert.Equal(TimeSpan.FromSeconds(1), hint);
        Assert.Null(await presence.GetConnectionIdAsync(rejectedId, timeout.Token));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{DeviceHub.Route}?deviceId={rejectedId}");
        request.Headers.Add("Connection", "Upgrade");
        request.Headers.Add("Upgrade", "websocket");
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        using var response = await http.SendAsync(request, timeout.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(timeout.Token);
        Assert.Equal(429, problem?.Status);
        Assert.Equal("Healthy", await http.GetStringAsync("/health", timeout.Token));
        using var negotiation = await http.PostAsync($"{DeviceHub.Route}/negotiate?negotiateVersion=1", null, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, negotiation.StatusCode);
        time.Advance(TimeSpan.FromSeconds(1));
        await rejected.StartAsync(timeout.Token);
        await WaitForPresenceAsync(presence, rejectedId, timeout.Token);

        builder.Configuration["AdmissionControl:Enabled"] = "false";
        ((IConfigurationRoot)builder.Configuration).Reload();
        await using var bypassed = CreateConnection(address, DeviceId.New(), _ => { });
        await bypassed.StartAsync(timeout.Token);
        builder.Configuration["AdmissionControl:Enabled"] = "true";
        ((IConfigurationRoot)builder.Configuration).Reload();
        await using var limitedAgain = CreateConnection(address, DeviceId.New(), _ => { });
        await Assert.ThrowsAsync<AggregateException>(() => limitedAgain.StartAsync(timeout.Token));
        Assert.Equal(HubConnectionState.Connected, first.State);
        Assert.Equal(HubConnectionState.Connected, rejected.State);
        Assert.Equal(HubConnectionState.Connected, bypassed.State);
        await first.StopAsync(timeout.Token);
        await rejected.StopAsync(timeout.Token);
        await bypassed.StopAsync(timeout.Token);
        await app.StopAsync(timeout.Token);
    }

    private static HubConnection CreateConnection(string address, DeviceId id, Action<TimeSpan> onRejected) =>
        new HubConnectionBuilder().WithUrl($"{address}{DeviceHub.Route}?deviceId={id}", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.WebSocketFactory = (context, token) => AdmissionRetry.ConnectAsync(context.Uri, onRejected, token);
        }).Build();

    private static async Task WaitForPresenceAsync(IDevicePresence presence, DeviceId id, CancellationToken cancellationToken)
    {
        while (await presence.GetConnectionIdAsync(id, cancellationToken) is null)
            await Task.Delay(10, cancellationToken);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }

    private sealed class NoCommands : IDeviceCommandDelivery
    {
        public Task DeliverAsync(DeliverCommand delivery, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeliverPendingAsync(DeviceId deviceId, string connectionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoReports : ICommandReportSink
    {
        public Task PublishAsync(CommandAcknowledged acknowledgement, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PublishAsync(CommandCompleted result, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
