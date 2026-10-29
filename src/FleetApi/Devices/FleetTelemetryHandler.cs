using FleetPulse.Contracts.Messaging;

namespace FleetPulse.FleetApi.Devices;

public static class FleetTelemetryHandler
{
    public static async Task HandleAsync(DeviceHeartbeatBatch batch, RedisFleetState state,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in batch.Messages.Chunk(250))
            await Task.WhenAll(chunk.Select(message => state.RecordAsync(message, cancellationToken)));
    }

    public static async Task HandleAsync(DeviceTelemetryBatch batch, RedisFleetState state,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in batch.Messages.Chunk(250))
            await Task.WhenAll(chunk.Select(message => state.RecordAsync(message, cancellationToken)));
    }
}
