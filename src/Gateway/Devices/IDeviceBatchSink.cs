using FleetPulse.Contracts.Messaging;

namespace FleetPulse.Gateway.Devices;

public interface IDeviceBatchSink
{
    Task PublishAsync(DeviceHeartbeatBatch batch, CancellationToken cancellationToken);
    Task PublishAsync(DeviceTelemetryBatch batch, CancellationToken cancellationToken);
}
