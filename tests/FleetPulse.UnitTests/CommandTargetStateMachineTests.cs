using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.FleetApi.Commands;

namespace FleetPulse.UnitTests;

public sealed class CommandTargetStateMachineTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RetryRequiresTenSecondsAndPreservesCommandId()
    {
        var target = NewTarget();
        var id = target.Id;

        Assert.False(CommandTargetStateMachine.Retry(target, Start.AddMinutes(1), Start.AddSeconds(9)));
        Assert.True(CommandTargetStateMachine.Retry(target, Start.AddMinutes(1), Start.AddSeconds(10)));
        Assert.Equal(id, target.Id);
        Assert.Equal(1, target.RetryCount);
        Assert.Equal(Start.AddSeconds(10), target.LastSentAtUtc);
        Assert.False(CommandTargetStateMachine.Retry(target, Start.AddMinutes(1), Start.AddSeconds(19)));
    }

    [Fact]
    public void AcknowledgementStopsRetriesAndDuplicatesDoNotChangeTimestamp()
    {
        var target = NewTarget();
        var now = Start.AddSeconds(2);

        Assert.True(CommandTargetStateMachine.Acknowledge(target, Start.AddMinutes(1), now));
        Assert.False(CommandTargetStateMachine.Acknowledge(target, Start.AddMinutes(1), now.AddSeconds(1)));
        Assert.False(CommandTargetStateMachine.Retry(target, Start.AddMinutes(1), now.AddSeconds(20)));
        Assert.Equal(now, target.AcknowledgedAtUtc);
    }

    [Theory]
    [InlineData(CommandOutcome.Succeeded, CommandTargetStatus.Succeeded)]
    [InlineData(CommandOutcome.Failed, CommandTargetStatus.Failed)]
    public void ResultFinalizesPendingOrAcknowledgedTarget(CommandOutcome outcome,
        CommandTargetStatus expected)
    {
        var target = NewTarget();
        Assert.True(CommandTargetStateMachine.Complete(target, Start.AddMinutes(1), outcome,
            "diagnostic result", Start.AddSeconds(1)));
        Assert.Equal(expected, target.Status);
        Assert.Equal(Start.AddSeconds(1), target.AcknowledgedAtUtc);
        Assert.Equal(Start.AddSeconds(1), target.CompletedAtUtc);
        Assert.Equal("diagnostic result", target.ResultDetail);
        Assert.False(CommandTargetStateMachine.Acknowledge(target, Start.AddMinutes(1), Start.AddSeconds(2)));
        Assert.False(CommandTargetStateMachine.Complete(target, Start.AddMinutes(1),
            CommandOutcome.Failed, "duplicate", Start.AddSeconds(2)));
        Assert.Equal(expected, target.Status);
    }

    [Fact]
    public void ExpiryStopsRetryAndLateReports()
    {
        var target = NewTarget();
        var expiry = Start.AddSeconds(10);

        Assert.False(CommandTargetStateMachine.Retry(target, expiry, expiry));
        Assert.False(CommandTargetStateMachine.Acknowledge(target, expiry, expiry));
        Assert.True(CommandTargetStateMachine.Expire(target, expiry, expiry));
        Assert.False(CommandTargetStateMachine.Complete(target, expiry,
            CommandOutcome.Succeeded, null, expiry.AddSeconds(1)));
        Assert.Equal(CommandTargetStatus.Expired, target.Status);
    }

    [Fact]
    public void ResultDetailFitsDatabaseColumn()
    {
        var target = NewTarget();

        Assert.True(CommandTargetStateMachine.Complete(target, Start.AddMinutes(1),
            CommandOutcome.Failed, new string('x', 600), Start.AddSeconds(1)));
        Assert.Equal(512, target.ResultDetail?.Length);
    }

    private static CommandTarget NewTarget() => new()
    {
        Id = FleetPulse.Contracts.Ids.CommandId.New(),
        CreatedAtUtc = Start,
        LastSentAtUtc = Start
    };
}
