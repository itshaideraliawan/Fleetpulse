using FleetPulse.Contracts.Messaging;

namespace FleetPulse.Gateway.Devices;

public interface ICommandReportSink
{
    Task PublishAsync(CommandAcknowledged acknowledgement, CancellationToken cancellationToken);
    Task PublishAsync(CommandCompleted result, CancellationToken cancellationToken);
}
