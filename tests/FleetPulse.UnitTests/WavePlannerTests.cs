using FleetPulse.Contracts.Ids;
using FleetPulse.FleetApi.Rollouts;

namespace FleetPulse.UnitTests;

public sealed class WavePlannerTests
{
    private static readonly int[] Percentages = [1, 10, 50, 100];
    private static readonly int[] WaveNumbers = [1, 2, 3, 4];

    [Theory]
    [InlineData(1, 1, 0, 0, 0)]
    [InlineData(2, 1, 0, 0, 1)]
    [InlineData(9, 1, 0, 4, 4)]
    [InlineData(10, 1, 0, 4, 5)]
    [InlineData(99, 1, 9, 40, 49)]
    [InlineData(100, 1, 9, 40, 50)]
    [InlineData(101, 2, 9, 40, 50)]
    [InlineData(5000, 50, 450, 2000, 2500)]
    public void CumulativeMilestonesCoverEveryDeviceExactlyOnce(int count,
        int first, int second, int third, int fourth)
    {
        var ids = Enumerable.Range(1, count).Select(Id).ToArray();
        var waves = WavePlanner.Plan(ids);

        Assert.Equal(new[] { first, second, third, fourth }, waves.Select(wave => wave.DeviceIds.Count));
        Assert.Equal(Percentages, waves.Select(wave => wave.CumulativePercentage));
        Assert.Equal(WaveNumbers, waves.Select(wave => wave.Number));
        var targets = waves.SelectMany(wave => wave.DeviceIds).ToArray();
        Assert.Equal(count, targets.Distinct().Count());
        Assert.Equal(ids.OrderBy(id => id.Value), targets);
    }

    [Fact]
    public void AssignmentIsIndependentOfInputOrder()
    {
        var ids = Enumerable.Range(1, 101).Select(Id).ToArray();
        var forward = WavePlanner.Plan(ids);
        var reverse = WavePlanner.Plan(ids.Reverse().ToArray());

        foreach (var pair in forward.Zip(reverse))
        {
            Assert.Equal(pair.First.DeviceIds, pair.Second.DeviceIds);
        }
    }

    [Fact]
    public void EmptyDuplicateAndUninitializedTargetsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => WavePlanner.Plan(null!));
        Assert.Throws<ArgumentException>(() => WavePlanner.Plan([]));
        Assert.Throws<ArgumentException>(() => WavePlanner.Plan([Id(1), Id(1)]));
        Assert.Throws<ArgumentException>(() => WavePlanner.Plan([DeviceId.From(Guid.Empty)]));
    }

    private static DeviceId Id(int sequence) =>
        DeviceId.From(new Guid(sequence, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
}
