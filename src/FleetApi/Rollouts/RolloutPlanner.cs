using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;
using FleetPulse.FleetApi.Demo;

namespace FleetPulse.FleetApi.Rollouts;

public sealed class RolloutPlanner(FleetDbContext db, IClock clock)
{
    public async Task<Rollout> CreateAsync(ReleaseId releaseId,
        IReadOnlyCollection<DeviceId> deviceIds, CancellationToken cancellationToken)
    {
        var plannedWaves = WavePlanner.Plan(deviceIds);
        if (!await db.Releases.AnyAsync(release => release.Id == releaseId, cancellationToken))
        {
            throw new ArgumentException("The release does not exist.", nameof(releaseId));
        }

        var ids = plannedWaves.SelectMany(wave => wave.DeviceIds).ToArray();
        var devices = await db.Devices.AsNoTracking().Where(device => ids.Contains(device.Id))
            .Select(device => new { device.Id, device.FirmwareVersion }).ToArrayAsync(cancellationToken);
        if (devices.Length != ids.Length)
        {
            throw new ArgumentException("Every target device must exist.", nameof(deviceIds));
        }

        var firmwareVersions = devices.ToDictionary(device => device.Id, device => device.FirmwareVersion);
        var rollout = new Rollout
        {
            Id = RolloutId.New(), ReleaseId = releaseId, CreatedAtUtc = clock.UtcNow
        };
        foreach (var planned in plannedWaves)
        {
            var wave = new Wave
            {
                Id = WaveId.New(), RolloutId = rollout.Id,
                Number = planned.Number, CumulativePercentage = planned.CumulativePercentage
            };
            foreach (var deviceId in planned.DeviceIds)
            {
                wave.Targets.Add(new WaveTarget
                {
                    RolloutId = rollout.Id, WaveId = wave.Id, DeviceId = deviceId,
                    OriginalFirmwareVersion = firmwareVersions[deviceId]
                });
            }

            rollout.Waves.Add(wave);
        }

        db.Rollouts.Add(rollout);
        await DemoControlState.TouchWorkAsync(db, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return rollout;
    }
}
