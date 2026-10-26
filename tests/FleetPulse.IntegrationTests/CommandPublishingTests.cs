using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Wolverine;

namespace FleetPulse.IntegrationTests;

public sealed class CommandPublishingTests
{
    [Fact]
    public async Task PersistsTargetsAndPublishesEveryBatchThroughOutbox()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await using var rabbitMq = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
        await Task.WhenAll(sqlServer.StartAsync(timeout.Token), rabbitMq.StartAsync(timeout.Token));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:rabbitmq"] = rabbitMq.GetConnectionString()
        });
        builder.Services.AddDbContext<FleetDbContext>(options =>
            options.UseSqlServer(sqlServer.GetConnectionString()));
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddScoped<CommandPublisher>();
        builder.UseWolverine(options =>
        {
            FleetMessaging.Configure(options);
            FleetMessaging.ConfigureOutbox(options, sqlServer.GetConnectionString());
        });

        using var host = builder.Build();
        var deviceIds = Enumerable.Range(0, CommandPublisher.BatchSize * 2 + 5)
            .Select(_ => DeviceId.New()).ToArray();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            await db.Database.MigrateAsync(timeout.Token);
            var region = new Region { Id = 1, Name = "Test" };
            var site = new Site { Id = 1, Name = "Test Site", Region = region };
            region.Sites.Add(site);
            for (var index = 0; index < deviceIds.Length; index++)
            {
                site.Devices.Add(new Device
                {
                    Id = deviceIds[index], Site = site, SerialNumber = $"FP-{index:00000}",
                    Model = "Kiosk-A", FirmwareVersion = "1.0.0"
                });
            }

            db.Regions.Add(region);
            await db.SaveChangesAsync(timeout.Token);
        }

        await host.StartAsync(timeout.Token);
        Guid commandGroupId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var publisher = scope.ServiceProvider.GetRequiredService<CommandPublisher>();
            var result = await publisher.CreateAsync(new CreateCommandRequest(
                CommandKind.RunDiagnostics, null, 1, null, false, 300), timeout.Token);
            Assert.NotNull(result);
            Assert.Equal(deviceIds.Length, result.TargetCount);
            commandGroupId = result.CommandId;
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            Assert.Equal(1, await db.Commands.CountAsync(timeout.Token));
            Assert.Equal(deviceIds.Length, await db.CommandTargets.CountAsync(timeout.Token));
            Assert.Equal(deviceIds.Length, await db.CommandTargets
                .Where(target => target.CommandGroupId == commandGroupId &&
                    target.Status == CommandTargetStatus.Pending)
                .CountAsync(timeout.Token));
        }

        var factory = new ConnectionFactory { Uri = new Uri(rabbitMq.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync(timeout.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
        var received = new HashSet<Guid>();
        while (received.Count < deviceIds.Length)
        {
            var delivery = await channel.BasicGetAsync(FleetMessaging.CommandQueue,
                autoAck: true, timeout.Token);
            if (delivery is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
                continue;
            }

            using var document = JsonDocument.Parse(delivery.Body);
            var command = document.RootElement.GetProperty("command");
            Assert.Equal((int)CommandKind.RunDiagnostics,
                command.GetProperty("kind").GetInt32());
            received.Add(command.GetProperty("commandId").GetGuid());
        }

        Assert.Equal(deviceIds.Length, received.Count);
        await host.StopAsync(timeout.Token);
    }
}
