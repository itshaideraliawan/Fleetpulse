using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using Microsoft.AspNetCore.SignalR;

namespace FleetPulse.Gateway.Devices;

public sealed partial class GatewayCommandDelivery(
    IDevicePresence presence,
    PendingDeviceCommands pending,
    IHubContext<DeviceHub> hub,
    IClock clock,
    ILogger<GatewayCommandDelivery> logger) : IDeviceCommandDelivery
{
    public const string ReceiveMethod = "ReceiveCommand";

    public async Task DeliverAsync(DeliverCommand delivery, CancellationToken cancellationToken)
    {
        var command = delivery.Command;
        var deviceId = command.Header.DeviceId;
        if (command.Header.ProtocolVersion != DeviceProtocolVersion.Current ||
            deviceId.Value == Guid.Empty || command.CommandId.Value == Guid.Empty ||
            command.ExpiresAtUtc <= clock.UtcNow)
        {
            LogDiscarded(logger, deviceId, command.CommandId);
            return;
        }

        var connectionId = await presence.GetConnectionIdAsync(deviceId, cancellationToken);
        if (connectionId is not null)
        {
            await SendAsync(command, connectionId, cancellationToken);
            await pending.RemoveAsync(deviceId, command.CommandId, cancellationToken);
            return;
        }

        await pending.SaveAsync(command, cancellationToken);
        connectionId = await presence.GetConnectionIdAsync(deviceId, cancellationToken);
        if (connectionId is not null)
        {
            await DeliverPendingAsync(deviceId, connectionId, cancellationToken);
        }
    }

    public async Task DeliverPendingAsync(DeviceId deviceId, string connectionId,
        CancellationToken cancellationToken)
    {
        foreach (var command in await pending.GetAsync(deviceId, cancellationToken))
        {
            if (command.ExpiresAtUtc > clock.UtcNow &&
                command.Header.ProtocolVersion == DeviceProtocolVersion.Current &&
                command.Header.DeviceId == deviceId)
            {
                await SendAsync(command, connectionId, cancellationToken);
            }

            await pending.RemoveAsync(deviceId, command.CommandId, cancellationToken);
        }
    }

    private async Task SendAsync(DeviceCommand command, string connectionId,
        CancellationToken cancellationToken)
    {
        await hub.Clients.Client(connectionId).SendAsync(ReceiveMethod, command, cancellationToken);
        LogDelivered(logger, command.Header.DeviceId, command.CommandId);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Command forwarded to device {DeviceId} {CommandId}")]
    private static partial void LogDelivered(ILogger logger, DeviceId deviceId, CommandId commandId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Invalid or expired command discarded {DeviceId} {CommandId}")]
    private static partial void LogDiscarded(ILogger logger, DeviceId deviceId, CommandId commandId);
}
