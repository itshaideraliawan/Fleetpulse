using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Simulator;

namespace FleetPulse.UnitTests;

public sealed class DeviceCommandExecutorTests
{
    [Fact]
    public async Task ConcurrentDuplicatesExecuteOnceAndReplayStoredResult()
    {
        var device = VirtualDevice.Create(1);
        var executor = new DeviceCommandExecutor(device, 0);
        var command = CreateCommand(device.Id);

        var executions = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => executor.ExecuteAsync(command, CancellationToken.None)));

        Assert.Equal(1, executor.ExecutionCount);
        Assert.Single(executions, execution => !execution.Replayed);
        Assert.All(executions, execution =>
        {
            Assert.Equal(CommandOutcome.Succeeded, execution.Result.Outcome);
            Assert.Equal(executions[0].Result, execution.Result);
        });
    }

    [Fact]
    public async Task FailureRateOneProducesFailureAndReplaysIt()
    {
        var device = VirtualDevice.Create(2);
        var executor = new DeviceCommandExecutor(device, 1);
        var command = CreateCommand(device.Id);

        var first = await executor.ExecuteAsync(command, CancellationToken.None);
        var repeated = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.Equal(CommandOutcome.Failed, first.Result.Outcome);
        Assert.Equal(first.Result, repeated.Result);
        Assert.False(first.Replayed);
        Assert.True(repeated.Replayed);
        Assert.Equal(1, executor.ExecutionCount);
    }

    [Fact]
    public async Task CancelledAttemptCanExecuteOnRedelivery()
    {
        var device = VirtualDevice.Create(3);
        var executor = new DeviceCommandExecutor(device, 0);
        var command = CreateCommand(device.Id);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(command, cancelled.Token));
        var retried = await executor.ExecuteAsync(command, CancellationToken.None);

        Assert.False(retried.Replayed);
        Assert.Equal(CommandOutcome.Succeeded, retried.Result.Outcome);
        Assert.Equal(2, executor.ExecutionCount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.25")]
    [InlineData("1")]
    public void AcceptsFailureRatesFromZeroToOne(string rate)
    {
        Assert.True(SimulatorOptions.TryParse(["--command-failure-rate", rate],
            "http://localhost:5000", out var options, out _));
        Assert.InRange(options!.CommandFailureRate, 0, 1);
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    [InlineData("NaN")]
    public void RejectsInvalidFailureRates(string rate)
    {
        Assert.False(SimulatorOptions.TryParse(["--command-failure-rate", rate],
            "http://localhost:5000", out _, out _));
    }

    private static DeviceCommand CreateCommand(DeviceId deviceId) =>
        new(new DeviceMessageHeader(DeviceProtocolVersion.Current, deviceId,
            DateTimeOffset.UtcNow), CommandId.New(), CommandKind.RunDiagnostics,
            DateTimeOffset.UtcNow.AddMinutes(5), null, null, null, null);
}
