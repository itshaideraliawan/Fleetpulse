using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using StackExchange.Redis;

namespace FleetPulse.FleetApi.Devices;

public sealed class RedisFleetState(IConnectionMultiplexer redis, IClock clock)
{
    public const string CountsKey = "fleet:counts";
    public const string ExpirationsKey = "fleet:device-expirations";
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(45);
    public const int ExpirationBatchSize = 500;

    private const string UpdateScript = """
        local state = KEYS[1]
        local expirations = KEYS[2]
        local counts = KEYS[3]
        local kind = ARGV[2]
        local sent = tonumber(ARGV[3])
        local previous = tonumber(redis.call('HGET', state, kind) or '-1')
        if sent <= previous then return 0 end

        local old_health = redis.call('HGET', state, 'health')
        local health_time = tonumber(redis.call('HGET', state, 'healthSentAt') or '-1')
        if sent < health_time then return 0 end
        if redis.call('HGET', state, 'online') == '0' and sent <= health_time then
            return 0
        end
        local new_health = old_health
        if sent >= health_time then
            new_health = ARGV[5]
            redis.call('HSET', state, 'health', new_health, 'healthSentAt', sent)
        end

        if redis.call('HGET', state, 'online') ~= '1' then
            redis.call('HINCRBY', counts, 'online', 1)
            redis.call('HINCRBY', counts, new_health, 1)
        elseif new_health ~= old_health then
            redis.call('HINCRBY', counts, old_health, -1)
            redis.call('HINCRBY', counts, new_health, 1)
        end

        redis.call('HSET', state, kind, sent, 'online', '1', 'lastSeenAt', ARGV[4])
        if kind == 'lastTelemetryAt' then
            redis.call('HSET', state, 'cpuPercent', ARGV[6], 'memoryPercent', ARGV[7],
                'temperatureCelsius', ARGV[8])
        end
        redis.call('ZADD', expirations, tonumber(ARGV[4]) + 45000, ARGV[1])
        return 1
        """;

    private const string ExpireScript = """
        local devices = redis.call('ZRANGEBYSCORE', KEYS[1], '-inf', ARGV[1], 'LIMIT', 0, ARGV[2])
        for _, device in ipairs(devices) do
            redis.call('ZREM', KEYS[1], device)
            local state = 'fleet:device:' .. device
            if redis.call('HGET', state, 'online') == '1' then
                redis.call('HSET', state, 'online', '0')
                redis.call('HINCRBY', KEYS[2], 'online', -1)
                local health = redis.call('HGET', state, 'health')
                redis.call('HINCRBY', KEYS[2], health, -1)
            end
        end
        return #devices
        """;

    public Task RecordAsync(Heartbeat message, CancellationToken cancellationToken) =>
        RecordAsync(message.Header, message.Health, "lastHeartbeatAt", null, cancellationToken);

    public Task RecordAsync(Telemetry message, CancellationToken cancellationToken) =>
        RecordAsync(message.Header, message.Health, "lastTelemetryAt", message, cancellationToken);

    public async Task<int> MarkExpiredAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await redis.GetDatabase().ScriptEvaluateAsync(ExpireScript,
            [ExpirationsKey, CountsKey],
            [clock.UtcNow.ToUnixTimeMilliseconds(), ExpirationBatchSize]);
        return (int)result;
    }

    private async Task RecordAsync(DeviceMessageHeader header, DeviceHealth health, string kind,
        Telemetry? telemetry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var deviceId = header.DeviceId.ToString();
        var now = clock.UtcNow.ToUnixTimeMilliseconds();
        var healthName = health.ToString().ToLowerInvariant();
        await redis.GetDatabase().ScriptEvaluateAsync(UpdateScript,
            [DeviceKey(header.DeviceId), ExpirationsKey, CountsKey],
            [deviceId, kind, header.SentAtUtc.ToUnixTimeMilliseconds(), now, healthName,
                telemetry?.CpuPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                telemetry?.MemoryPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                telemetry?.TemperatureCelsius.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty]);
    }

    public static RedisKey DeviceKey(DeviceId deviceId) => $"fleet:device:{deviceId}";
}
