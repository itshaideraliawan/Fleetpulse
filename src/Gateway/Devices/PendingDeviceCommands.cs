using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using StackExchange.Redis;

namespace FleetPulse.Gateway.Devices;

public sealed class PendingDeviceCommands(IConnectionMultiplexer redis)
{
    private const string SaveWithExpiryScript = """
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        if redis.call('PTTL', KEYS[1]) < tonumber(ARGV[3]) then
            redis.call('PEXPIRE', KEYS[1], ARGV[3])
        end
        return 1
        """;

    public async Task SaveAsync(DeviceCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().ScriptEvaluateAsync(SaveWithExpiryScript,
            [Key(command.Header.DeviceId)],
            [command.CommandId.ToString(), JsonSerializer.Serialize(command),
                (long)TimeSpan.FromDays(1).TotalMilliseconds]);
    }

    public async Task<IReadOnlyList<DeviceCommand>> GetAsync(DeviceId deviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await redis.GetDatabase().HashGetAllAsync(Key(deviceId));
        return entries.Select(entry => JsonSerializer.Deserialize<DeviceCommand>(entry.Value.ToString())!)
            .ToArray();
    }

    public async Task RemoveAsync(DeviceId deviceId, CommandId commandId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().HashDeleteAsync(Key(deviceId), commandId.ToString());
    }

    private static RedisKey Key(DeviceId deviceId) => $"device:pending-commands:{deviceId}";
}
