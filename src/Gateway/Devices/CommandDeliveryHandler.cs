using FleetPulse.Contracts.Messaging;

namespace FleetPulse.Gateway.Devices;

public static class CommandDeliveryHandler
{
    public static Task HandleAsync(DeliverCommand delivery, IDeviceCommandDelivery dispatcher,
        CancellationToken cancellationToken) =>
        dispatcher.DeliverAsync(delivery, cancellationToken);
}
