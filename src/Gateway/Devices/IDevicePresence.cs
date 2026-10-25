using FleetPulse.Contracts.Ids;

namespace FleetPulse.Gateway.Devices;

public interface IDevicePresence
{
    Task ConnectAsync(DeviceId deviceId, string connectionId, CancellationToken cancellationToken);
    Task DisconnectAsync(DeviceId deviceId, string connectionId, CancellationToken cancellationToken);
    Task<string?> GetConnectionIdAsync(DeviceId deviceId, CancellationToken cancellationToken);
}
