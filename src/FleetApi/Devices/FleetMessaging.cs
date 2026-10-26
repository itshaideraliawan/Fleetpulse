using Wolverine;
using Wolverine.RabbitMQ;

namespace FleetPulse.FleetApi.Devices;

public static class FleetMessaging
{
    public const string HeartbeatQueue = "fleetpulse.device-heartbeats";
    public const string TelemetryQueue = "fleetpulse.device-telemetry";

    public static void Configure(WolverineOptions options)
    {
        options.Discovery.IncludeAssembly(typeof(FleetTelemetryHandler).Assembly);
        options.UseRabbitMqUsingNamedConnection("rabbitmq").AutoProvision();
        options.ListenToRabbitQueue(HeartbeatQueue).ProcessInline();
        options.ListenToRabbitQueue(TelemetryQueue).ProcessInline();
    }
}
