using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;

namespace FleetPulse.Gateway.Devices;

public interface IDeviceCommandDelivery
{
    Task DeliverAsync(DeliverCommand delivery, CancellationToken cancellationToken);
    Task DeliverPendingAsync(DeviceId deviceId, string connectionId,
        CancellationToken cancellationToken);
}
