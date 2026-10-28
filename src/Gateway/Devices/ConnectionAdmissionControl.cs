using Microsoft.Extensions.Options;

namespace FleetPulse.Gateway.Devices;

public sealed class AdmissionControlOptions
{
    public const string SectionName = "AdmissionControl";
    public bool Enabled { get; set; } = true;
    public int PermitsPerSecond { get; set; } = 100;
    public int BurstCapacity { get; set; } = 100;
}

public readonly record struct AdmissionDecision(bool Allowed, int RetryAfterSeconds);

public sealed class ConnectionAdmissionControl(IOptionsMonitor<AdmissionControlOptions> options, TimeProvider timeProvider)
{
    private readonly Lock _lock = new();
    private double _tokens;
    private long _lastTimestamp;
    private int _capacity;
    private int _rate;
    private int _demoEnabled = -1;
    public bool Enabled
    {
        get
        {
            var enabled = Volatile.Read(ref _demoEnabled);
            return enabled >= 0 ? enabled == 1 : options.CurrentValue.Enabled;
        }
    }
    public void SetDemoEnabled(bool enabled) => Volatile.Write(ref _demoEnabled, enabled ? 1 : 0);

    public AdmissionDecision TryAcquire()
    {
        var settings = options.CurrentValue;
        if (!Enabled) return new AdmissionDecision(true, 0);

        lock (_lock)
        {
            var now = timeProvider.GetTimestamp();
            if (_capacity == 0)
            {
                _tokens = settings.BurstCapacity;
            }
            else
            {
                // Refill at the previous rate before applying a reloaded configuration.
                _tokens = Math.Min(_capacity, _tokens + timeProvider.GetElapsedTime(_lastTimestamp, now).TotalSeconds * _rate);
                _tokens = Math.Min(_tokens, settings.BurstCapacity);
            }
            _lastTimestamp = now;
            _capacity = settings.BurstCapacity;
            _rate = settings.PermitsPerSecond;
            if (_tokens >= 1)
            {
                _tokens--;
                return new AdmissionDecision(true, 0);
            }

            return new AdmissionDecision(false, Math.Max(1, (int)Math.Ceiling((1 - _tokens) / _rate)));
        }
    }
}
