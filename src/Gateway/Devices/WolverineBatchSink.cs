using FleetPulse.Contracts.Messaging;
using Wolverine;

namespace FleetPulse.Gateway.Devices;

public sealed class WolverineBatchSink(IServiceScopeFactory scopeFactory) : IDeviceBatchSink
{
    public async Task PublishAsync(DeviceHeartbeatBatch batch, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        cancellationToken.ThrowIfCancellationRequested();
        await bus.PublishAsync(batch);
    }

    public async Task PublishAsync(DeviceTelemetryBatch batch, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        cancellationToken.ThrowIfCancellationRequested();
        await bus.PublishAsync(batch);
    }
}
