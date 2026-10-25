using FleetPulse.Contracts.Ids;
using StackExchange.Redis;

namespace FleetPulse.Gateway.Devices;

public sealed class RedisDevicePresence(IConnectionMultiplexer redis) : IDevicePresence
{
    private const string RemoveIfCurrentScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";

    public async Task ConnectAsync(DeviceId deviceId, string connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().StringSetAsync(Key(deviceId), connectionId);
    }

    public async Task DisconnectAsync(DeviceId deviceId, string connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().ScriptEvaluateAsync(
            RemoveIfCurrentScript,
            [Key(deviceId)],
            [connectionId]);
    }

    public async Task<string?> GetConnectionIdAsync(DeviceId deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await redis.GetDatabase().StringGetAsync(Key(deviceId));
        return value.IsNull ? null : value.ToString();
    }

    private static RedisKey Key(DeviceId deviceId) => $"device:presence:{deviceId}";
}
