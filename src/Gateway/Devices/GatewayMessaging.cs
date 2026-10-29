using FleetPulse.Contracts.Messaging;
using Wolverine;
using Wolverine.RabbitMQ;

namespace FleetPulse.Gateway.Devices;

public static class GatewayMessaging
{
    public const string HeartbeatQueue = "fleetpulse.device-heartbeats";
    public const string TelemetryQueue = "fleetpulse.device-telemetry";
    public const string CommandQueue = "fleetpulse.deliver-command";
    public const string CommandAckQueue = "fleetpulse.command-acknowledged";
    public const string CommandResultQueue = "fleetpulse.command-completed";

    public static void Configure(WolverineOptions options)
    {
        options.Discovery.IncludeAssembly(typeof(CommandDeliveryHandler).Assembly);
        options.UseRabbitMqUsingNamedConnection("rabbitmq")
            .ConfigureChannelCreation(channel =>
            {
                channel.PublisherConfirmationsEnabled = true;
                channel.PublisherConfirmationTrackingEnabled = true;
            })
            .AutoProvision();
        options.PublishMessage<DeviceHeartbeatBatch>().ToRabbitQueue(HeartbeatQueue).SendInline();
        options.PublishMessage<DeviceTelemetryBatch>().ToRabbitQueue(TelemetryQueue).SendInline();
        options.PublishMessage<CommandAcknowledged>().ToRabbitQueue(CommandAckQueue).SendInline();
        options.PublishMessage<CommandCompleted>().ToRabbitQueue(CommandResultQueue).SendInline();
        options.ListenToRabbitQueue(CommandQueue).ProcessInline().ListenerCount(8);
    }
}
