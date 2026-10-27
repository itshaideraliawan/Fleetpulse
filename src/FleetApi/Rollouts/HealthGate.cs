using FleetPulse.FleetApi.Commands;

namespace FleetPulse.FleetApi.Rollouts;

public static class HealthGate
{
    public static HealthGateResult Evaluate(IReadOnlyCollection<CommandTargetStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (statuses.Any(status => !Enum.IsDefined(status))) throw new ArgumentException("Unknown target status.", nameof(statuses));
        var failures = statuses.Count(status => status is CommandTargetStatus.Failed or CommandTargetStatus.Expired);
        var complete = statuses.Count > 0 && statuses.All(status => status is
            CommandTargetStatus.Succeeded or CommandTargetStatus.Failed or CommandTargetStatus.Expired);
        return new HealthGateResult(complete, complete && (long)failures * 100 < (long)statuses.Count * 2,
            statuses.Count, failures);
    }
}

public sealed record HealthGateResult(bool IsComplete, bool Passed, int Total, int Failures);
