using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Simulator;
using FleetPulse.Gateway.Devices;
using System.Text.Json;

namespace FleetPulse.UnitTests;

public sealed class SimulatorDemoControlTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(999, false)]
    public void DemoAcknowledgementsUseTheDeviceProtocolValidator(int version, bool expected)
    {
        var id = DeviceId.New();
        var payload = JsonSerializer.SerializeToElement(new DemoControlApplied(
            new DeviceMessageHeader(version, id, DateTimeOffset.UtcNow), 1), JsonSerializerOptions.Web);
        Assert.Equal(expected, DeviceMessageValidator.TryValidate<DemoControlApplied>(payload, id, out _, out _));
    }
    [Fact]
    public async Task InitialSnapshotPreservesStartupFaultUntilAControlRequestOverridesIt()
    {
        var device = VirtualDevice.Create(2);
        var executor = new DeviceCommandExecutor(device, 0, "2.4.0", "Kiosk-B");
        var initial = new DemoControlSnapshot(0, true, null, null, 0, 0, null);
        await executor.ApplyDemoAsync(initial, CancellationToken.None);
        Assert.Equal(CommandOutcome.Failed, (await executor.ExecuteAsync(Command(device, "2.4.0"), CancellationToken.None)).Result.Outcome);
        await executor.ApplyDemoAsync(initial with { Revision = 1 }, CancellationToken.None);
        Assert.Equal(CommandOutcome.Succeeded, (await executor.ExecuteAsync(Command(device, "2.4.0"), CancellationToken.None)).Result.Outcome);
    }
    [Fact]
    public async Task RuntimeInjectionOverridesStartupOptionsAndIgnoresStaleSettings()
    {
        var device = VirtualDevice.Create(2);
        var executor = new DeviceCommandExecutor(device, 0);
        var state = new DemoControlSnapshot(1, true, "2.4.0", "Kiosk-B", 0, 0, null);
        await executor.ApplyDemoAsync(state, CancellationToken.None);
        Assert.Equal(CommandOutcome.Failed, (await executor.ExecuteAsync(Command(device, "2.4.0"), CancellationToken.None)).Result.Outcome);
        await executor.ApplyDemoAsync(state with { Revision = 2, BadReleaseVersion = null, BadReleaseModel = null }, CancellationToken.None);
        await executor.ApplyDemoAsync(state, CancellationToken.None);
        Assert.Equal(CommandOutcome.Succeeded, (await executor.ExecuteAsync(Command(device, "2.4.0"), CancellationToken.None)).Result.Outcome);
        Assert.Equal("2.4.0", device.FirmwareVersion);
    }

    [Fact]
    public async Task ResetFollowsInFlightExecutionAndCannotBeUndoneByOldCommandsOrReplay()
    {
        var device = VirtualDevice.Create(1);
        var executor = new DeviceCommandExecutor(device, 0);
        var old = Command(device, "2.4.0");
        var execution = executor.ExecuteAsync(old, CancellationToken.None);
        var resetAt = DateTimeOffset.UtcNow.AddMilliseconds(1);
        var state = new DemoControlSnapshot(1, true, null, null, 0, 1, resetAt);
        await executor.ApplyDemoAsync(state, CancellationToken.None);
        await execution;
        Assert.Equal("1.0.0", device.FirmwareVersion);
        await executor.ExecuteAsync(old, CancellationToken.None);
        Assert.Equal("1.0.0", device.FirmwareVersion);
        var delayed = Command(device, "2.5.0") with { Header = old.Header };
        Assert.Equal(CommandOutcome.Failed, (await executor.ExecuteAsync(delayed, CancellationToken.None)).Result.Outcome);
        var current = Command(device, "2.6.0");
        await executor.ExecuteAsync(current, CancellationToken.None);
        await executor.ApplyDemoAsync(state, CancellationToken.None);
        Assert.Equal("2.6.0", device.FirmwareVersion);
    }

    private static DeviceCommand Command(VirtualDevice device, string version) => new(
        new DeviceMessageHeader(DeviceProtocolVersion.Current, device.Id, DateTimeOffset.UtcNow),
        CommandId.New(), CommandKind.InstallFirmware, DateTimeOffset.UtcNow.AddMinutes(5),
        ReleaseId.New(), RolloutId.New(), WaveId.New(), version);
}
