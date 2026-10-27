using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Commands;

public sealed class CommandQueries(FleetDbContext db)
{
    private static IQueryable<CommandSummary> Summaries(IQueryable<Command> commands) =>
        commands.Select(command => new CommandSummary(command.Id, command.Kind, command.CreatedAtUtc,
            command.ExpiresAtUtc, command.Targets.Count,
            command.Targets.Count(target => target.Status == CommandTargetStatus.Pending),
            command.Targets.Count(target => target.Status == CommandTargetStatus.Acknowledged),
            command.Targets.Count(target => target.Status == CommandTargetStatus.Succeeded),
            command.Targets.Count(target => target.Status == CommandTargetStatus.Failed),
            command.Targets.Count(target => target.Status == CommandTargetStatus.Expired)));

    public Task<CommandSummary[]> RecentAsync(CancellationToken cancellationToken) =>
        Summaries(db.Commands.AsNoTracking().OrderByDescending(command => command.CreatedAtUtc)
            .ThenBy(command => command.Id).Take(20)).ToArrayAsync(cancellationToken);

    public async Task<CommandProgress?> GetAsync(Guid id, int page, int pageSize,
        CommandTargetStatus? status, CancellationToken cancellationToken)
    {
        var summary = await Summaries(db.Commands.AsNoTracking().Where(command => command.Id == id))
            .SingleOrDefaultAsync(cancellationToken);
        if (summary is null) return null;

        var targets = db.CommandTargets.AsNoTracking().Where(target => target.CommandGroupId == id);
        if (status is not null) targets = targets.Where(target => target.Status == status);
        var total = await targets.CountAsync(cancellationToken);
        var items = await targets.Join(db.Devices, target => target.DeviceId, device => device.Id,
                (target, device) => new { Target = target, Device = device })
            .OrderBy(item => item.Device.SerialNumber).ThenBy(item => item.Target.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new CommandTargetResult(item.Target.Id, item.Target.DeviceId,
                item.Device.SerialNumber, item.Device.Model, item.Device.Site.Region.Name, item.Target.Status,
                item.Target.RetryCount, item.Target.AcknowledgedAtUtc, item.Target.CompletedAtUtc, item.Target.ResultDetail))
            .ToArrayAsync(cancellationToken);
        return new CommandProgress(summary, total, page, pageSize, items);
    }
}

public sealed record CommandSummary(Guid Id, CommandKind Kind, DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc, int Total, int Pending, int Acknowledged, int Succeeded, int Failed, int Expired);

public sealed record CommandProgress(CommandSummary Summary, int Total, int Page, int PageSize,
    IReadOnlyList<CommandTargetResult> Items);

public sealed record CommandTargetResult(CommandId Id, DeviceId DeviceId, string SerialNumber,
    string Model, string RegionName, CommandTargetStatus Status, int RetryCount,
    DateTimeOffset? AcknowledgedAtUtc, DateTimeOffset? CompletedAtUtc, string? Detail);
