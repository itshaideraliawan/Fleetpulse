using FleetPulse.Contracts.DeviceProtocol;

namespace FleetPulse.FleetApi.Commands;

public static class CommandTargetStateMachine
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    public static bool Retry(CommandTarget target, DateTimeOffset expiresAtUtc,
        DateTimeOffset now)
    {
        if (target.Status != CommandTargetStatus.Pending || now >= expiresAtUtc ||
            now - target.LastSentAtUtc < RetryInterval)
        {
            return false;
        }

        target.LastSentAtUtc = now;
        target.RetryCount++;
        return true;
    }

    public static bool Acknowledge(CommandTarget target, DateTimeOffset expiresAtUtc,
        DateTimeOffset now)
    {
        if (target.Status != CommandTargetStatus.Pending || now >= expiresAtUtc)
        {
            return false;
        }

        target.Status = CommandTargetStatus.Acknowledged;
        target.AcknowledgedAtUtc = now;
        return true;
    }

    public static bool Complete(CommandTarget target, DateTimeOffset expiresAtUtc,
        CommandOutcome outcome, string? detail, DateTimeOffset now)
    {
        if (target.Status is not (CommandTargetStatus.Pending or CommandTargetStatus.Acknowledged) ||
            now >= expiresAtUtc)
        {
            return false;
        }

        target.Status = outcome == CommandOutcome.Succeeded
            ? CommandTargetStatus.Succeeded : CommandTargetStatus.Failed;
        target.AcknowledgedAtUtc ??= now;
        target.CompletedAtUtc = now;
        target.ResultDetail = detail?.Length > 512 ? detail[..512] : detail;
        return true;
    }

    public static bool Expire(CommandTarget target, DateTimeOffset expiresAtUtc,
        DateTimeOffset now)
    {
        if (target.Status is not (CommandTargetStatus.Pending or CommandTargetStatus.Acknowledged) ||
            now < expiresAtUtc)
        {
            return false;
        }

        target.Status = CommandTargetStatus.Expired;
        return true;
    }
}
