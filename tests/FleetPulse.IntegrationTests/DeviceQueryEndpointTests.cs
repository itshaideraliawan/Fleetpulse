using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.MsSql;
using Testcontainers.Redis;

namespace FleetPulse.IntegrationTests;

public sealed class DeviceQueryEndpointTests
{
    [Fact]
    public async Task SummarySearchDetailAndOpenApiReflectSqlAndRedis()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await using var redisContainer = new RedisBuilder("redis:7.4-alpine").Build();
        await Task.WhenAll(sqlServer.StartAsync(timeout.Token), redisContainer.StartAsync(timeout.Token));
        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisContainer.GetConnectionString());

        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlServer(sqlServer.GetConnectionString()).Options;
        await using (var db = new FleetDbContext(options))
        {
            await db.Database.MigrateAsync(timeout.Token);
            await SeedAsync(db, timeout.Token);
        }

        var clock = new TestClock { UtcNow = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) };
        var state = new RedisFleetState(redis, clock);
        await state.RecordAsync(new Telemetry(
            new DeviceMessageHeader(DeviceProtocolVersion.Current, Id(1), clock.UtcNow),
            DeviceHealth.Healthy, 20, 30, 40), timeout.Token);
        await state.RecordAsync(Heartbeat(3, DeviceHealth.Degraded, clock.UtcNow), timeout.Token);
        clock.UtcNow = clock.UtcNow.AddSeconds(30);
        await state.RecordAsync(Heartbeat(1, DeviceHealth.Healthy, clock.UtcNow), timeout.Token);
        clock.UtcNow = clock.UtcNow.AddSeconds(15);
        await state.MarkExpiredAsync(timeout.Token);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<FleetDbContext>(database =>
            database.UseSqlServer(sqlServer.GetConnectionString()));
        builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        builder.Services.AddScoped<DeviceQueries>();
        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi();
        await using var app = builder.Build();
        app.MapDeviceEndpoints();
        app.MapOpenApi();
        await app.StartAsync(timeout.Token);

        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };

        var summary = await http.GetFromJsonAsync<FleetSummary>("/api/fleet/summary", timeout.Token);
        Assert.NotNull(summary);
        Assert.Equal(3, summary.TotalDevices);
        Assert.Equal(1, summary.OnlineDevices);
        Assert.Equal(2, summary.OfflineDevices);
        Assert.Equal(1, summary.HealthyDevices);
        Assert.Equal(0, summary.DegradedDevices);
        Assert.Equal(1, summary.Regions.Single(region => region.RegionId == 1).OnlineDevices);
        Assert.Equal(0, summary.Regions.Single(region => region.RegionId == 2).OnlineDevices);

        var unfiltered = await http.GetFromJsonAsync<DevicePage>(
            "/api/devices?page=2&pageSize=1", timeout.Token);
        Assert.NotNull(unfiltered);
        Assert.Equal(3, unfiltered.Total);
        Assert.Equal("FP-00002", Assert.Single(unfiltered.Items).SerialNumber);

        var page = await http.GetFromJsonAsync<DevicePage>(
            "/api/devices?regionId=1&status=online&health=healthy&pageSize=1", timeout.Token);
        Assert.NotNull(page);
        Assert.Equal(1, page.Total);
        Assert.Equal("FP-00001", Assert.Single(page.Items).SerialNumber);
        var offline = await http.GetFromJsonAsync<DevicePage>(
            "/api/devices?status=offline&health=degraded&q=FP-00003", timeout.Token);
        Assert.NotNull(offline);
        Assert.Equal(1, offline.Total);

        var detail = await http.GetFromJsonAsync<DeviceDetail>(
            $"/api/devices/{Id(3)}", timeout.Token);
        Assert.NotNull(detail);
        Assert.False(detail.Device.Online);
        Assert.Equal("degraded", detail.Device.Health);
        Assert.NotNull(detail.Device.LastSeenAt);
        var liveDetail = await http.GetFromJsonAsync<DeviceDetail>(
            $"/api/devices/{Id(1)}", timeout.Token);
        Assert.NotNull(liveDetail);
        Assert.Equal(20, liveDetail.CpuPercent);

        var invalid = await http.GetAsync("/api/devices?pageSize=101", timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        var missing = await http.GetAsync($"/api/devices/{Guid.NewGuid()}", timeout.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);

        using var document = await http.GetFromJsonAsync<JsonDocument>("/openapi/v1.json", timeout.Token);
        Assert.NotNull(document);
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/fleet/summary", out _));
        Assert.True(paths.TryGetProperty("/api/devices", out _));
        Assert.True(paths.TryGetProperty("/api/devices/{id}", out _));

        await app.StopAsync(timeout.Token);
    }

    private static async Task SeedAsync(FleetDbContext db, CancellationToken cancellationToken)
    {
        var north = new Region { Id = 1, Name = "North" };
        var south = new Region { Id = 2, Name = "South" };
        var northSite = new Site { Id = 1, Name = "North Site", Region = north };
        var southSite = new Site { Id = 2, Name = "South Site", Region = south };
        db.Devices.AddRange(
            new Device { Id = Id(1), SerialNumber = "FP-00001", Model = "Kiosk-A", FirmwareVersion = "1.0.0", Site = northSite },
            new Device { Id = Id(2), SerialNumber = "FP-00002", Model = "Kiosk-B", FirmwareVersion = "1.0.0", Site = northSite },
            new Device { Id = Id(3), SerialNumber = "FP-00003", Model = "Kiosk-C", FirmwareVersion = "1.0.0", Site = southSite });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Heartbeat Heartbeat(int sequence, DeviceHealth health, DateTimeOffset sentAt) =>
        new(new DeviceMessageHeader(DeviceProtocolVersion.Current, Id(sequence), sentAt), health);

    private static DeviceId Id(int sequence)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(sequence).CopyTo(bytes, 0);
        return DeviceId.From(new Guid(bytes));
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }
}
