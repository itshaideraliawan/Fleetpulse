using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Devices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace FleetPulse.IntegrationTests;

public sealed class CommandQueryEndpointTests
{
    [Fact]
    public async Task HistoryProgressAndFilteredResultsReflectPersistedTargets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await sql.StartAsync(timeout.Token);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<FleetDbContext>(options => options.UseSqlServer(sql.GetConnectionString()));
        builder.Services.AddScoped<CommandQueries>();
        builder.Services.AddScoped<CommandPublisher>();
        builder.Services.AddFleetOpenApi();
        await using var app = builder.Build();
        app.MapCommandEndpoints();
        app.MapOpenApi();
        var commandId = Guid.NewGuid();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            await db.Database.MigrateAsync(timeout.Token);
            var site = new Site { Id = 1, Name = "Test site", Region = new Region { Id = 1, Name = "North" } };
            var command = new Command
            {
                Id = commandId, Kind = CommandKind.RunDiagnostics,
                CreatedAtUtc = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero),
                ExpiresAtUtc = new DateTimeOffset(2026, 10, 27, 1, 0, 0, TimeSpan.Zero)
            };
            for (var index = 0; index < 5; index++)
            {
                var deviceId = DeviceId.New();
                db.Devices.Add(new Device
                {
                    Id = deviceId, SerialNumber = $"FP-{index:00000}", Model = "Kiosk-A",
                    FirmwareVersion = "1.0.0", Site = site
                });
                command.Targets.Add(new CommandTarget
                {
                    Id = CommandId.New(), DeviceId = deviceId, Status = (CommandTargetStatus)index,
                    CreatedAtUtc = command.CreatedAtUtc, LastSentAtUtc = command.CreatedAtUtc,
                    ResultDetail = index == 3 ? "Diagnostic failure" : null, RetryCount = 2
                });
            }
            db.Commands.Add(command);
            await db.SaveChangesAsync(timeout.Token);
        }
        await app.StartAsync(timeout.Token);
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        var history = await http.GetFromJsonAsync<CommandSummary[]>("/api/commands", timeout.Token);
        var summary = Assert.Single(history!);
        Assert.Equal(5, summary.Total);
        Assert.Equal(1, summary.Pending);
        Assert.Equal(1, summary.Acknowledged);
        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Expired);
        var page = await http.GetFromJsonAsync<CommandProgress>(
            $"/api/commands/{commandId}?page=2&pageSize=2", timeout.Token);
        Assert.NotNull(page);
        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("FP-00002", page.Items[0].SerialNumber);
        var failed = await http.GetFromJsonAsync<CommandProgress>(
            $"/api/commands/{commandId}?status=3", timeout.Token);
        Assert.NotNull(failed);
        Assert.Equal(5, failed.Summary.Total);
        Assert.Equal(1, failed.Total);
        Assert.Equal("Diagnostic failure", Assert.Single(failed.Items).Detail);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            var pending = await db.CommandTargets.SingleAsync(
                target => target.Status == CommandTargetStatus.Pending, timeout.Token);
            Assert.True(CommandTargetStateMachine.Complete(pending,
                new DateTimeOffset(2026, 10, 27, 1, 0, 0, TimeSpan.Zero),
                CommandOutcome.Succeeded, "Healthy", pending.CreatedAtUtc.AddSeconds(1)));
            await db.SaveChangesAsync(timeout.Token);
        }
        var refreshed = await http.GetFromJsonAsync<CommandProgress>(
            $"/api/commands/{commandId}", timeout.Token);
        Assert.NotNull(refreshed);
        Assert.Equal(0, refreshed.Summary.Pending);
        Assert.Equal(2, refreshed.Summary.Succeeded);
        foreach (var query in new[] { "page=0", "pageSize=101", "status=42" })
        {
            using var invalid = await http.GetAsync($"/api/commands/{commandId}?{query}", timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        }
        using var missing = await http.GetAsync($"/api/commands/{Guid.NewGuid()}", timeout.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var document = await http.GetFromJsonAsync<JsonDocument>("/openapi/v1.json", timeout.Token);
        Assert.NotNull(document);
        Assert.Equal("uuid", document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("CommandId").GetProperty("format").GetString());
        await app.StopAsync(timeout.Token);
    }
}
