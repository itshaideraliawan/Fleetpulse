using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Gateway.Devices;
using Microsoft.Extensions.Logging.Abstractions;

namespace FleetPulse.UnitTests;

public sealed class DeviceMessagePublisherTests
{
    [Fact]
    public async Task FlushGroupsMessagesByKindAndCapsBatchSize()
    {
        var buffer = new DeviceMessageBuffer();
        var sink = new RecordingSink();
        var publisher = new DeviceMessagePublisher(buffer, sink, NullLogger<DeviceMessagePublisher>.Instance);
        var header = new DeviceMessageHeader(1, DeviceId.New(), DateTimeOffset.UnixEpoch);

        for (var index = 0; index < DeviceMessagePublisher.MaxBatchSize + 1; index++)
        {
            Assert.True(buffer.TryWrite(new Heartbeat(header, DeviceHealth.Healthy)));
        }

        Assert.True(buffer.TryWrite(new Telemetry(header, DeviceHealth.Healthy, 1, 2, 3)));
        await publisher.FlushAsync(CancellationToken.None);

        Assert.Equal([DeviceMessagePublisher.MaxBatchSize, 1], sink.Heartbeats.Select(batch => batch.Messages.Count));
        Assert.Single(sink.Telemetry);
        Assert.Single(sink.Telemetry[0].Messages);
    }

    private sealed class RecordingSink : IDeviceBatchSink
    {
        public List<DeviceHeartbeatBatch> Heartbeats { get; } = [];
        public List<DeviceTelemetryBatch> Telemetry { get; } = [];

        public Task PublishAsync(DeviceHeartbeatBatch batch, CancellationToken cancellationToken)
        {
            Heartbeats.Add(batch);
            return Task.CompletedTask;
        }

        public Task PublishAsync(DeviceTelemetryBatch batch, CancellationToken cancellationToken)
        {
            Telemetry.Add(batch);
            return Task.CompletedTask;
        }
    }
}
