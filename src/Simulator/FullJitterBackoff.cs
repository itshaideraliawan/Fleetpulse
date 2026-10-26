namespace FleetPulse.Simulator;

public static class FullJitterBackoff
{
    public static TimeSpan NextDelay(int failureCount, double randomSample)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failureCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(randomSample);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(randomSample, 1);

        var ceilingSeconds = Math.Min(30, Math.Pow(2, Math.Min(failureCount - 1, 5)));
        return TimeSpan.FromSeconds(ceilingSeconds * randomSample);
    }
}
