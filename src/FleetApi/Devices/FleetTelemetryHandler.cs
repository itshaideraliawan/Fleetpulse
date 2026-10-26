using FleetPulse.Contracts.Messaging;

namespace FleetPulse.FleetApi.Devices;

public static class FleetTelemetryHandler
{
    public static async Task HandleAsync(DeviceHeartbeatBatch batch, RedisFleetState state,
        CancellationToken cancellationToken)
    {
        foreach (var message in batch.Messages)
        {
            await state.RecordAsync(message, cancellationToken);
        }
    }

    public static async Task HandleAsync(DeviceTelemetryBatch batch, RedisFleetState state,
        CancellationToken cancellationToken)
    {
        foreach (var message in batch.Messages)
        {
            await state.RecordAsync(message, cancellationToken);
        }
    }
}
