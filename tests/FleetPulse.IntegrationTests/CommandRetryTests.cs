using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Wolverine;

namespace FleetPulse.IntegrationTests;

public sealed class CommandRetryTests
{
    [Fact]
    public async Task RetriesSameIdThenStopsAfterAcknowledgementAndStoresResult()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await using var rabbitMq = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
        await Task.WhenAll(sqlServer.StartAsync(timeout.Token), rabbitMq.StartAsync(timeout.Token));

        var clock = new TestClock(new DateTimeOffset(2026, 10, 27, 0, 0, 20, TimeSpan.Zero));
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:rabbitmq"] = rabbitMq.GetConnectionString()
        });
        builder.Services.AddDbContext<FleetDbContext>(options =>
            options.UseSqlServer(sqlServer.GetConnectionString()));
        builder.Services.AddSingleton<IClock>(clock);
        builder.UseWolverine(options =>
        {
            FleetMessaging.Configure(options);
            FleetMessaging.ConfigureOutbox(options, sqlServer.GetConnectionString());
        });

        using var host = builder.Build();
        var deviceId = DeviceId.New();
        var commandId = CommandId.New();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            await db.Database.MigrateAsync(timeout.Token);
            var region = new Region { Id = 1, Name = "Test" };
            var site = new Site { Id = 1, Name = "Test", Region = region };
            db.Devices.Add(new Device
            {
                Id = deviceId, Site = site, SerialNumber = "FP-00001",
                Model = "Kiosk-A", FirmwareVersion = "1.0.0"
            });
            db.Commands.Add(new Command
            {
                Id = Guid.NewGuid(), Kind = CommandKind.RunDiagnostics,
                CreatedAtUtc = clock.UtcNow.AddSeconds(-10),
                ExpiresAtUtc = clock.UtcNow.AddMinutes(1),
                Targets =
                {
                    new CommandTarget
                    {
                        Id = commandId, DeviceId = deviceId,
                        CreatedAtUtc = clock.UtcNow.AddSeconds(-10),
                        LastSentAtUtc = clock.UtcNow.AddSeconds(-10)
                    }
                }
            });
            await db.SaveChangesAsync(timeout.Token);
        }

        await host.StartAsync(timeout.Token);
        var worker = new CommandRetryWorker(
            host.Services.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<CommandRetryWorker>.Instance);
        await worker.ScanAsync(timeout.Token);

        var factory = new ConnectionFactory { Uri = new Uri(rabbitMq.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync(timeout.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
        JsonElement command;
        while (true)
        {
            var delivery = await channel.BasicGetAsync(FleetMessaging.CommandQueue,
                autoAck: true, timeout.Token);
            if (delivery is null)
            {
                await Task.Delay(100, timeout.Token);
                continue;
            }

            using var document = JsonDocument.Parse(delivery.Body);
            command = document.RootElement.GetProperty("command").Clone();
            break;
        }

        Assert.Equal(commandId.Value, command.GetProperty("commandId").GetGuid());
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            var ack = new CommandAcknowledged(new CommandAck(
                new DeviceMessageHeader(DeviceProtocolVersion.Current, deviceId, clock.UtcNow),
                commandId));
            await CommandReportHandler.HandleAsync(ack, db, clock, timeout.Token);
            await CommandReportHandler.HandleAsync(ack, db, clock, timeout.Token);
            var result = new CommandCompleted(new CommandResult(ack.Message.Header,
                commandId, CommandOutcome.Succeeded, "healthy"));
            await CommandReportHandler.HandleAsync(result, db, clock, timeout.Token);
            await CommandReportHandler.HandleAsync(result, db, clock, timeout.Token);
        }

        clock.UtcNow = clock.UtcNow.AddSeconds(20);
        await worker.ScanAsync(timeout.Token);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
            var target = await db.CommandTargets.SingleAsync(timeout.Token);
            Assert.Equal(CommandTargetStatus.Succeeded, target.Status);
            Assert.Equal(1, target.RetryCount);
            Assert.Equal("healthy", target.ResultDetail);
            Assert.Equal(commandId, target.Id);
        }

        Assert.Null(await channel.BasicGetAsync(FleetMessaging.CommandQueue,
            autoAck: true, timeout.Token));
        await host.StopAsync(timeout.Token);
    }

    private sealed class TestClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
