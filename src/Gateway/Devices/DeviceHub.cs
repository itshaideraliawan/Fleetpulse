using FleetPulse.Contracts.Ids;
using Microsoft.AspNetCore.SignalR;

namespace FleetPulse.Gateway.Devices;

public sealed partial class DeviceHub(IDevicePresence presence, ILogger<DeviceHub> logger) : Hub
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Device connected {DeviceId} {ConnectionId}")]
    private static partial void LogConnected(ILogger logger, DeviceId deviceId, string connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device disconnected {DeviceId} {ConnectionId}")]
    private static partial void LogDisconnected(ILogger logger, DeviceId deviceId, string connectionId);
}
