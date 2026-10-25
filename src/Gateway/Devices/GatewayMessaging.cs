using FleetPulse.Contracts.Messaging;
using JasperFx.CodeGeneration;
using Wolverine;
using Wolverine.RabbitMQ;

namespace FleetPulse.Gateway.Devices;

public static class GatewayMessaging
{
    public const string HeartbeatQueue = "fleetpulse.device-heartbeats";
    public const string TelemetryQueue = "fleetpulse.device-telemetry";

    public static void Configure(WolverineOptions options)
    {
        // The Gateway only publishes; no generated Wolverine handlers are needed here.
        options.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
        options.UseRabbitMqUsingNamedConnection("rabbitmq")
            .ConfigureChannelCreation(channel =>
            {
                channel.PublisherConfirmationsEnabled = true;
                channel.PublisherConfirmationTrackingEnabled = true;
            })
            .AutoProvision();
        options.PublishMessage<DeviceHeartbeatBatch>().ToRabbitQueue(HeartbeatQueue).SendInline();
        options.PublishMessage<DeviceTelemetryBatch>().ToRabbitQueue(TelemetryQueue).SendInline();
    }
}
