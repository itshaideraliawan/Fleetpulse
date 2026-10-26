using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Simulator;

namespace FleetPulse.UnitTests;

public sealed class SimulatorTests
{
    [Theory]
    [InlineData(1, "00000001-0000-0000-0000-000000000000", "Kiosk-A")]
    [InlineData(2, "00000002-0000-0000-0000-000000000000", "Kiosk-B")]
    [InlineData(5000, "00001388-0000-0000-0000-000000000000", "Kiosk-B")]
    public void DeviceIdentityMatchesFleetSeed(int sequence, string expectedId, string expectedModel)
    {
        var device = VirtualDevice.Create(sequence);

        Assert.Equal(expectedId, device.Id.ToString());
        Assert.Equal(expectedModel, device.Model);
        Assert.Equal("1.0.0", device.FirmwareVersion);
    }

    [Fact]
    public void HealthProfilesAndMessagesUseCurrentProtocol()
    {
        var healthy = VirtualDevice.Create(1);
        var degraded = VirtualDevice.Create(91);
        var unhealthy = VirtualDevice.Create(99);
        var now = DateTimeOffset.UnixEpoch;

        Assert.Equal(DeviceHealth.Healthy, healthy.Health);
        Assert.Equal(DeviceHealth.Degraded, degraded.Health);
        Assert.Equal(DeviceHealth.Unhealthy, unhealthy.Health);
        Assert.Equal(DeviceProtocolVersion.Current, degraded.CreateHeartbeat(now).Header.ProtocolVersion);
        Assert.Equal(degraded.Id, degraded.CreateTelemetry(now).Header.DeviceId);
    }

    [Fact]
    public void FullJitterStaysInsideAnExponentiallyGrowingCap()
    {
        Assert.Equal(TimeSpan.Zero, FullJitterBackoff.NextDelay(1, 0));
        Assert.Equal(TimeSpan.FromSeconds(0.5), FullJitterBackoff.NextDelay(1, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(4), FullJitterBackoff.NextDelay(4, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(15), FullJitterBackoff.NextDelay(20, 0.5));
    }

    [Fact]
    public void DisjointProcessRangesAreAcceptedAndOverlapBeyondFleetIsRejected()
    {
        Assert.True(SimulatorOptions.TryParse(["--start", "1", "--devices", "2500"],
            "http://localhost:5000", out var first, out _));
        Assert.True(SimulatorOptions.TryParse(["--start", "2501", "--devices", "2500"],
            "http://localhost:5000", out var second, out _));
        Assert.Equal(2500, first!.Start + first.Devices - 1);
        Assert.Equal(2501, second!.Start);
        Assert.False(SimulatorOptions.TryParse(["--start", "2501", "--devices", "2501"],
            "http://localhost:5000", out _, out _));
    }
}
