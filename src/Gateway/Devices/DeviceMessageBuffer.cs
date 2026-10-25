using System.Threading.Channels;
using FleetPulse.Contracts.DeviceProtocol;

namespace FleetPulse.Gateway.Devices;

public sealed class DeviceMessageBuffer
{
    public const int Capacity = 10_000;

    private readonly Channel<QueuedDeviceMessage> _channel = Channel.CreateBounded<QueuedDeviceMessage>(
        new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    public ChannelReader<QueuedDeviceMessage> Reader => _channel.Reader;

    public bool TryWrite(Heartbeat message) => _channel.Writer.TryWrite(new QueuedDeviceMessage.HeartbeatItem(message));

    public bool TryWrite(Telemetry message) => _channel.Writer.TryWrite(new QueuedDeviceMessage.TelemetryItem(message));
}

public abstract record QueuedDeviceMessage
{
    public sealed record HeartbeatItem(Heartbeat Value) : QueuedDeviceMessage;
    public sealed record TelemetryItem(Telemetry Value) : QueuedDeviceMessage;
}
