using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace FleetPulse.Gateway.Devices;

public sealed partial class DeviceHub(
    IDevicePresence presence,
    IDeviceCommandDelivery commandDelivery,
    ICommandReportSink commandReports,
    DeviceMessageBuffer buffer,
    ILogger<DeviceHub> logger) : Hub
{
    public const string Route = "/hubs/device";

    public override async Task OnConnectedAsync()
    {
        var rawDeviceId = Context.GetHttpContext()?.Request.Query["deviceId"].ToString();
        if (!Guid.TryParse(rawDeviceId, out var guid) || guid == Guid.Empty)
        {
            Context.Abort();
            return;
        }

        var deviceId = DeviceId.From(guid);
        await presence.ConnectAsync(deviceId, Context.ConnectionId, Context.ConnectionAborted);
        Context.Items[typeof(DeviceId)] = deviceId;
        await commandDelivery.DeliverPendingAsync(deviceId, Context.ConnectionId,
            Context.ConnectionAborted);
        LogConnected(logger, deviceId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(typeof(DeviceId), out var value) && value is DeviceId deviceId)
        {
            await presence.DisconnectAsync(deviceId, Context.ConnectionId, CancellationToken.None);
            LogDisconnected(logger, deviceId, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public void SendHeartbeat(JsonElement payload)
    {
        if (!TryGetDeviceId(out var deviceId))
        {
            DeviceMessageMetrics.RecordDropped("heartbeat", "not_connected");
            return;
        }

        if (!DeviceMessageValidator.TryValidate<Heartbeat>(payload, deviceId, out var message, out var reason))
        {
            DeviceMessageMetrics.RecordDropped("heartbeat", reason!);
            return;
        }

        if (!buffer.TryWrite(message!))
        {
            DeviceMessageMetrics.RecordDropped("heartbeat", "queue_full");
            return;
        }

        DeviceMessageMetrics.RecordAccepted("heartbeat");
    }

    public void SendTelemetry(JsonElement payload)
    {
        if (!TryGetDeviceId(out var deviceId))
        {
            DeviceMessageMetrics.RecordDropped("telemetry", "not_connected");
            return;
        }

        if (!DeviceMessageValidator.TryValidate<Telemetry>(payload, deviceId, out var message, out var reason))
        {
            DeviceMessageMetrics.RecordDropped("telemetry", reason!);
            return;
        }

        if (!buffer.TryWrite(message!))
        {
            DeviceMessageMetrics.RecordDropped("telemetry", "queue_full");
            return;
        }

        DeviceMessageMetrics.RecordAccepted("telemetry");
    }

    public async Task SendCommandAck(JsonElement payload)
    {
        if (!TryGetDeviceId(out var deviceId))
        {
            DeviceMessageMetrics.RecordDropped("command_ack", "not_connected");
            return;
        }

        if (!DeviceMessageValidator.TryValidate<CommandAck>(payload, deviceId,
                out var message, out var reason) || message!.CommandId.Value == Guid.Empty)
        {
            DeviceMessageMetrics.RecordDropped("command_ack", reason ?? "command_id");
            return;
        }

        await commandReports.PublishAsync(new CommandAcknowledged(message), Context.ConnectionAborted);
        DeviceMessageMetrics.RecordAccepted("command_ack");
    }

    public async Task SendCommandResult(JsonElement payload)
    {
        if (!TryGetDeviceId(out var deviceId))
        {
            DeviceMessageMetrics.RecordDropped("command_result", "not_connected");
            return;
        }

        if (!DeviceMessageValidator.TryValidate<CommandResult>(payload, deviceId,
                out var message, out var reason) || message!.CommandId.Value == Guid.Empty)
        {
            DeviceMessageMetrics.RecordDropped("command_result", reason ?? "command_id");
            return;
        }

        await commandReports.PublishAsync(new CommandCompleted(message), Context.ConnectionAborted);
        DeviceMessageMetrics.RecordAccepted("command_result");
    }

    private bool TryGetDeviceId(out DeviceId deviceId)
    {
        if (Context.Items.TryGetValue(typeof(DeviceId), out var value) && value is DeviceId connectedId)
        {
            deviceId = connectedId;
            return true;
        }

        deviceId = default;
        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device connected {DeviceId} {ConnectionId}")]
    private static partial void LogConnected(ILogger logger, DeviceId deviceId, string connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device disconnected {DeviceId} {ConnectionId}")]
    private static partial void LogDisconnected(ILogger logger, DeviceId deviceId, string connectionId);
}
