using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Messaging;

namespace FleetPulse.Gateway.Devices;

public sealed partial class DeviceMessagePublisher(
    DeviceMessageBuffer buffer,
    IDeviceBatchSink sink,
    ILogger<DeviceMessagePublisher> logger) : BackgroundService
{
    public const int MaxBatchSize = 250;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping; the in-memory buffer cannot outlive this process.
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var heartbeats = new List<Heartbeat>(MaxBatchSize);
        var telemetry = new List<Telemetry>(MaxBatchSize);

        while (buffer.Reader.TryRead(out var queued))
        {
            switch (queued)
            {
                case QueuedDeviceMessage.HeartbeatItem item:
                    heartbeats.Add(item.Value);
                    if (heartbeats.Count == MaxBatchSize)
                    {
                        await PublishWithRetryAsync(new DeviceHeartbeatBatch(heartbeats.ToArray()), cancellationToken);
                        heartbeats.Clear();
                    }
                    break;

                case QueuedDeviceMessage.TelemetryItem item:
                    telemetry.Add(item.Value);
                    if (telemetry.Count == MaxBatchSize)
                    {
                        await PublishWithRetryAsync(new DeviceTelemetryBatch(telemetry.ToArray()), cancellationToken);
                        telemetry.Clear();
                    }
                    break;
            }
        }
        if (heartbeats.Count > 0)
        {
            await PublishWithRetryAsync(new DeviceHeartbeatBatch(heartbeats.ToArray()), cancellationToken);
        }

        if (telemetry.Count > 0)
        {
            await PublishWithRetryAsync(new DeviceTelemetryBatch(telemetry.ToArray()), cancellationToken);
        }
    }

    private async Task PublishWithRetryAsync(DeviceHeartbeatBatch message, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await sink.PublishAsync(message, cancellationToken);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogPublishRetry(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private async Task PublishWithRetryAsync(DeviceTelemetryBatch message, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await sink.PublishAsync(message, cancellationToken);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogPublishRetry(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device batch publish failed; retrying")]
    private static partial void LogPublishRetry(ILogger logger, Exception exception);
}
