using FleetPulse.Gateway.Devices;
using FleetPulse.Simulator;
using Microsoft.Extensions.Options;

namespace FleetPulse.UnitTests;

public sealed class AdmissionControlTests
{
    [Fact]
    public void BurstRefillsContinuouslyAndIdleTimeCannotExceedCapacity()
    {
        var time = new ManualTime();
        var monitor = new Settings(new AdmissionControlOptions { BurstCapacity = 2, PermitsPerSecond = 2 });
        var admission = new ConnectionAdmissionControl(monitor, time);
        Assert.True(admission.TryAcquire().Allowed);
        Assert.True(admission.TryAcquire().Allowed);
        Assert.Equal(new AdmissionDecision(false, 1), admission.TryAcquire());
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.False(admission.TryAcquire().Allowed);
        time.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True(admission.TryAcquire().Allowed);
        Assert.False(admission.TryAcquire().Allowed);
        time.Advance(TimeSpan.FromHours(1));
        Assert.True(admission.TryAcquire().Allowed);
        Assert.True(admission.TryAcquire().Allowed);
        Assert.False(admission.TryAcquire().Allowed);
    }

    [Fact]
    public void SwitchBypassesWithoutConsumingTokensAndReloadDoesNotMintTokens()
    {
        var time = new ManualTime();
        var monitor = new Settings(new AdmissionControlOptions { BurstCapacity = 1, PermitsPerSecond = 1 });
        var admission = new ConnectionAdmissionControl(monitor, time);
        Assert.True(admission.TryAcquire().Allowed);
        monitor.CurrentValue = new AdmissionControlOptions { Enabled = false, BurstCapacity = 1, PermitsPerSecond = 1 };
        Assert.All(Enumerable.Range(0, 10), _ => Assert.True(admission.TryAcquire().Allowed));
        monitor.CurrentValue = new AdmissionControlOptions { BurstCapacity = 10, PermitsPerSecond = 10 };
        Assert.False(admission.TryAcquire().Allowed);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(admission.TryAcquire().Allowed);
        Assert.False(admission.TryAcquire().Allowed);
    }

    [Fact]
    public void ParallelAttemptsShareOneBucketButInstancesAreIndependent()
    {
        var monitor = new Settings(new AdmissionControlOptions { BurstCapacity = 10, PermitsPerSecond = 1 });
        var first = new ConnectionAdmissionControl(monitor, new ManualTime());
        var second = new ConnectionAdmissionControl(monitor, new ManualTime());
        var admitted = 0;
        Parallel.For(0, 1000, _ => { if (first.TryAcquire().Allowed) Interlocked.Increment(ref admitted); });
        Assert.Equal(10, admitted);
        Assert.True(second.TryAcquire().Allowed);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    [InlineData(null, 0)]
    [InlineData("-1", 0)]
    [InlineData("invalid", 0)]
    [InlineData("999999999999", 0)]
    public void SimulatorParsesNumericRetryHints(string? value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), AdmissionRetry.ParseDelay(value));

    private sealed class ManualTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class Settings(AdmissionControlOptions value) : IOptionsMonitor<AdmissionControlOptions>
    {
        public AdmissionControlOptions CurrentValue { get; set; } = value;
        public AdmissionControlOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AdmissionControlOptions, string?> listener) => null;
    }
}
