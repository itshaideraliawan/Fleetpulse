using FleetPulse.Contracts.Ids;

namespace FleetPulse.FleetApi.Rollouts;

public static class WavePlanner
{
    private static readonly int[] Milestones = [1, 10, 50, 100];

    public static IReadOnlyList<PlannedWave> Plan(IReadOnlyCollection<DeviceId> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        if (deviceIds.Count == 0 || deviceIds.Any(id => id.Value == Guid.Empty))
        {
            throw new ArgumentException("At least one nonempty device ID is required.", nameof(deviceIds));
        }

        var ordered = deviceIds.OrderBy(id => id.Value).ToArray();
        if (ordered.Distinct().Count() != ordered.Length)
        {
            throw new ArgumentException("Each device can appear only once in a rollout.", nameof(deviceIds));
        }

        var waves = new List<PlannedWave>(Milestones.Length);
        var previous = 0;
        for (var index = 0; index < Milestones.Length; index++)
        {
            // Round cumulative totals up so even a small cohort has a canary.
            var end = (int)(((long)ordered.Length * Milestones[index] + 99) / 100);
            waves.Add(new PlannedWave(index + 1, Milestones[index], ordered[previous..end]));
            previous = end;
        }

        return waves;
    }
}

public sealed record PlannedWave(int Number, int CumulativePercentage, IReadOnlyList<DeviceId> DeviceIds);
