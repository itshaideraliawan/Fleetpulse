using FleetPulse.Contracts.Messaging;
using Wolverine;

namespace FleetPulse.Gateway.Devices;

public sealed class WolverineCommandReportSink(IServiceScopeFactory scopeFactory) : ICommandReportSink
{
    public async Task PublishAsync(CommandAcknowledged acknowledgement,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        cancellationToken.ThrowIfCancellationRequested();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(acknowledgement);
    }

    public async Task PublishAsync(CommandCompleted result, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        cancellationToken.ThrowIfCancellationRequested();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().PublishAsync(result);
    }
}
