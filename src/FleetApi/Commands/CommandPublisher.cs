using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace FleetPulse.FleetApi.Commands;

public sealed class CommandPublisher(IDbContextOutbox<FleetDbContext> outbox, IClock clock)
{
    public const int BatchSize = 100;

    public async Task<CreateCommandResult?> CreateAsync(CreateCommandRequest request,
        CancellationToken cancellationToken)
    {
        var db = outbox.DbContext;
        var query = db.Devices.AsNoTracking();
        if (request.DeviceId is Guid deviceId)
        {
            query = query.Where(device => device.Id == DeviceId.From(deviceId));
        }
        else
        {
            if (request.RegionId is int regionId)
            {
                query = query.Where(device => device.Site.RegionId == regionId);
            }

            if (request.Model is { } model)
            {
                query = query.Where(device => device.Model == model);
            }
        }

        var deviceIds = await query.OrderBy(device => device.Id)
            .Select(device => device.Id).ToArrayAsync(cancellationToken);
        if (deviceIds.Length == 0)
        {
            return null;
        }

        var now = clock.UtcNow;
        var expires = now.AddSeconds(request.ExpiresInSeconds ?? 300);
        var command = new Command
        {
            Id = Guid.NewGuid(),
            Kind = request.Kind,
            CreatedAtUtc = now,
            ExpiresAtUtc = expires
        };

        db.Commands.Add(command);
        foreach (var batch in deviceIds.Chunk(BatchSize))
        {
            foreach (var targetDeviceId in batch)
            {
                var targetId = CommandId.New();
                db.CommandTargets.Add(new CommandTarget
                {
                    Id = targetId,
                    CommandGroupId = command.Id,
                    DeviceId = targetDeviceId,
                    CreatedAtUtc = now,
                    LastSentAtUtc = now
                });
                var message = new DeviceCommand(
                    new DeviceMessageHeader(DeviceProtocolVersion.Current, targetDeviceId, now),
                    targetId, request.Kind, expires, null, null, null, null);
                await outbox.PublishAsync(new DeliverCommand(message));
            }

            await outbox.SaveChangesAndFlushMessagesAsync(MultiFlushMode.AllowMultiples,
                cancellationToken);
            db.ChangeTracker.Clear();
        }

        return new CreateCommandResult(command.Id, deviceIds.Length);
    }
}
