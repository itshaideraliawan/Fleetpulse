using FleetPulse.Contracts.Messaging;

namespace FleetPulse.Gateway.Devices;

public static class CommandDeliveryHandler
{
    public static Task HandleAsync(DeliverCommand delivery, IDeviceCommandDelivery dispatcher,
        CancellationToken cancellationToken) =>
        dispatcher.DeliverAsync(delivery, cancellationToken);

    public static Task HandleAsync(DeliverCommandBatch batch, IDeviceCommandDelivery dispatcher,
        CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(batch.Commands,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
            async (command, token) => await dispatcher.DeliverAsync(new DeliverCommand(command), token));
}
