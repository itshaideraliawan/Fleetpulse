using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Simulator;

namespace FleetPulse.UnitTests;

public sealed class SimulatorFirmwareTests
{
    [Fact]
    public async Task InstallRollbackAndOldResultReplayPreserveTheRestoredVersion()
    {
        var device = VirtualDevice.Create(1);
        var executor = new DeviceCommandExecutor(device, 1);
        var install = FirmwareCommand(device, CommandKind.InstallFirmware, "2.4.0");
        var deliveries = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => executor.ExecuteAsync(install, CancellationToken.None)));
        Assert.Equal("2.4.0", device.FirmwareVersion);
        Assert.Equal(1, executor.ExecutionCount);
        Assert.Single(deliveries, delivery => !delivery.Replayed);
        Assert.All(deliveries, delivery => Assert.Equal(deliveries[0].Result, delivery.Result));

        var rollback = await executor.ExecuteAsync(FirmwareCommand(device, CommandKind.RollbackFirmware, "1.0.0"), CancellationToken.None);
        Assert.Equal(CommandOutcome.Succeeded, rollback.Result.Outcome);
        Assert.Equal("1.0.0", device.FirmwareVersion);
        var old = await executor.ExecuteAsync(install, CancellationToken.None);
        Assert.True(old.Replayed);
        Assert.Equal(deliveries[0].Result, old.Result);
        Assert.Equal("1.0.0", device.FirmwareVersion);
        Assert.Equal(2, executor.ExecutionCount);
    }

    [Theory]
    [InlineData(1, "2.4.0", CommandOutcome.Succeeded)]
    [InlineData(2, "2.4.0", CommandOutcome.Failed)]
    [InlineData(3, "2.4.0", CommandOutcome.Succeeded)]
    [InlineData(2, "2.5.0", CommandOutcome.Succeeded)]
    public async Task InjectionMatchesBothVersionAndHardwareModel(int sequence, string version, CommandOutcome expected)
    {
        var device = VirtualDevice.Create(sequence);
        var executor = new DeviceCommandExecutor(device, 0, "2.4.0", "Kiosk-B");
        var command = FirmwareCommand(device, CommandKind.InstallFirmware, version);
        var result = await executor.ExecuteAsync(command, CancellationToken.None);
        Assert.Equal(expected, result.Result.Outcome);
        Assert.Equal(expected == CommandOutcome.Succeeded ? version : "1.0.0", device.FirmwareVersion);
        var duplicate = await executor.ExecuteAsync(command, CancellationToken.None);
        Assert.True(duplicate.Replayed);
        Assert.Equal(result.Result, duplicate.Result);
    }

    [Fact]
    public async Task RollbackIgnoresInjectionAndDiagnosticsFailureRate()
    {
        var device = VirtualDevice.Create(2);
        var executor = new DeviceCommandExecutor(device, 1, "1.0.0", "Kiosk-B");
        await executor.ExecuteAsync(FirmwareCommand(device, CommandKind.InstallFirmware, "2.4.0"), CancellationToken.None);
        var result = await executor.ExecuteAsync(FirmwareCommand(device, CommandKind.RollbackFirmware, "1.0.0"), CancellationToken.None);
        Assert.Equal(CommandOutcome.Succeeded, result.Result.Outcome);
        Assert.Equal("1.0.0", device.FirmwareVersion);
    }

    [Fact]
    public async Task CancelledQueuedCommandCannotLetRollbackOvertakeTheInstall()
    {
        var device = VirtualDevice.Create(1);
        var executor = new DeviceCommandExecutor(device, 0);
        var install = executor.ExecuteAsync(FirmwareCommand(device, CommandKind.InstallFirmware, "2.4.0"), CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var skipped = executor.ExecuteAsync(FirmwareCommand(device, CommandKind.InstallFirmware, "2.5.0"), cancelled.Token);
        var rollback = executor.ExecuteAsync(FirmwareCommand(device, CommandKind.RollbackFirmware, "1.0.0"), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => skipped);
        Assert.Equal(CommandOutcome.Succeeded, (await install).Result.Outcome);
        Assert.Equal(CommandOutcome.Succeeded, (await rollback).Result.Outcome);
        Assert.Equal("1.0.0", device.FirmwareVersion);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("rollout")]
    [InlineData("wave")]
    [InlineData("version")]
    [InlineData("long_version")]
    [InlineData("empty_release")]
    [InlineData("empty_rollout")]
    [InlineData("empty_wave")]
    public async Task InvalidFirmwareMetadataLeavesFirmwareUnchanged(string field)
    {
        var device = VirtualDevice.Create(1);
        var command = FirmwareCommand(device, CommandKind.InstallFirmware, "2.4.0");
        command = field switch
        {
            "release" => command with { ReleaseId = null },
            "rollout" => command with { RolloutId = null },
            "wave" => command with { WaveId = null },
            "version" => command with { FirmwareVersion = " " },
            "long_version" => command with { FirmwareVersion = new string('x', 41) },
            "empty_release" => command with { ReleaseId = ReleaseId.From(Guid.Empty) },
            "empty_rollout" => command with { RolloutId = RolloutId.From(Guid.Empty) },
            _ => command with { WaveId = WaveId.From(Guid.Empty) }
        };
        var result = await new DeviceCommandExecutor(device, 0).ExecuteAsync(command, CancellationToken.None);
        Assert.Equal(CommandOutcome.Failed, result.Result.Outcome);
        Assert.Equal("1.0.0", device.FirmwareVersion);
    }

    [Fact]
    public async Task ExpiryIsRecheckedBeforeFirmwareChanges()
    {
        var device = VirtualDevice.Create(1);
        var command = FirmwareCommand(device, CommandKind.InstallFirmware, "2.4.0")
            with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(1) };
        var result = await new DeviceCommandExecutor(device, 0).ExecuteAsync(command, CancellationToken.None);
        Assert.Equal(CommandOutcome.Failed, result.Result.Outcome);
        Assert.Contains("expired", result.Result.Detail);
        Assert.Equal("1.0.0", device.FirmwareVersion);
    }

    [Fact]
    public void AcceptsPairedInjectionOptionsAndDefaultsToHealthyFirmware()
    {
        Assert.True(SimulatorOptions.TryParse([], "http://localhost:5000", out var defaults, out _));
        Assert.Null(defaults!.BadReleaseVersion);
        Assert.Null(defaults.BadReleaseModel);
        Assert.True(SimulatorOptions.TryParse(["--bad-release-version", "2.4.0", "--bad-release-model", "Kiosk-B"],
            "http://localhost:5000", out var options, out _));
        Assert.Equal("2.4.0", options!.BadReleaseVersion);
        Assert.Equal("Kiosk-B", options.BadReleaseModel);
    }

    [Theory]
    [InlineData("--bad-release-version", "2.4.0")]
    [InlineData("--bad-release-model", "Kiosk-B")]
    [InlineData("--bad-release-model", "Kiosk-D")]
    public void RejectsUnpairedInjectionOptions(string option, string value)
    {
        Assert.False(SimulatorOptions.TryParse([option, value], "http://localhost:5000", out _, out _));
    }

    [Theory]
    [InlineData("", "Kiosk-B")]
    [InlineData(" ", "Kiosk-B")]
    [InlineData("2.4.0", "Kiosk-D")]
    [InlineData("2.4.0", "")]
    public void RejectsInvalidPairedInjectionOptions(string version, string model)
    {
        Assert.False(SimulatorOptions.TryParse(["--bad-release-version", version, "--bad-release-model", model],
            "http://localhost:5000", out _, out _));
    }

    private static DeviceCommand FirmwareCommand(VirtualDevice device, CommandKind kind, string version) => new(
        new DeviceMessageHeader(DeviceProtocolVersion.Current, device.Id, DateTimeOffset.UtcNow),
        CommandId.New(), kind, DateTimeOffset.UtcNow.AddMinutes(5), ReleaseId.New(), RolloutId.New(), WaveId.New(), version);
}
