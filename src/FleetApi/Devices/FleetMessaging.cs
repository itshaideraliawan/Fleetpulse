using FleetPulse.Contracts.Messaging;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;
using Wolverine.SqlServer;

namespace FleetPulse.FleetApi.Devices;

public static class FleetMessaging
{
    public const string HeartbeatQueue = "fleetpulse.device-heartbeats";
    public const string TelemetryQueue = "fleetpulse.device-telemetry";
    public const string CommandQueue = "fleetpulse.deliver-command";

    public static void Configure(WolverineOptions options)
    {
        options.Discovery.IncludeAssembly(typeof(FleetTelemetryHandler).Assembly);
        options.UseRabbitMqUsingNamedConnection("rabbitmq").AutoProvision();
        options.ListenToRabbitQueue(HeartbeatQueue).ProcessInline();
        options.ListenToRabbitQueue(TelemetryQueue).ProcessInline();
    }

    public static void ConfigureOutbox(WolverineOptions options, string connectionString)
    {
        options.PersistMessagesWithSqlServer(connectionString, "wolverine");
        options.UseEntityFrameworkCoreTransactions();
        options.Policies.UseDurableOutboxOnAllSendingEndpoints();
        options.PublishMessage<DeliverCommand>().ToRabbitQueue(CommandQueue).UseDurableOutbox();
    }
}
