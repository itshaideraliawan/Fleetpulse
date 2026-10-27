using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Rollouts;

namespace FleetPulse.UnitTests;

public sealed class HealthGateTests
{
    [Theory]
    [InlineData(100, 0, true)]
    [InlineData(100, 1, true)]
    [InlineData(100, 2, false)]
    [InlineData(100, 3, false)]
    [InlineData(50, 1, false)]
    [InlineData(51, 1, true)]
    [InlineData(1, 1, false)]
    [InlineData(5000, 99, true)]
    [InlineData(5000, 100, false)]
    public void FailureRateMustBeStrictlyBelowTwoPercent(int total, int failed, bool passed)
    {
        var statuses = Enumerable.Repeat(CommandTargetStatus.Failed, failed)
            .Concat(Enumerable.Repeat(CommandTargetStatus.Succeeded, total - failed)).ToArray();
        var result = HealthGate.Evaluate(statuses);
        Assert.True(result.IsComplete);
        Assert.Equal(passed, result.Passed);
        Assert.Equal(total, result.Total);
        Assert.Equal(failed, result.Failures);
    }

    [Theory]
    [InlineData(CommandTargetStatus.Pending)]
    [InlineData(CommandTargetStatus.Acknowledged)]
    public void UnfinishedTargetsPreventAHealthyDecision(CommandTargetStatus pending)
    {
        var result = HealthGate.Evaluate([CommandTargetStatus.Succeeded, pending]);
        Assert.False(result.IsComplete);
        Assert.False(result.Passed);
    }

    [Fact]
    public void EmptyCohortCannotPassAndExpiredTargetsCountAsFailures()
    {
        Assert.False(HealthGate.Evaluate([]).IsComplete);
        var expired = HealthGate.Evaluate([CommandTargetStatus.Expired]);
        Assert.True(expired.IsComplete);
        Assert.False(expired.Passed);
        Assert.Equal(1, expired.Failures);
        Assert.Throws<ArgumentException>(() => HealthGate.Evaluate([(CommandTargetStatus)99]));
    }
}
