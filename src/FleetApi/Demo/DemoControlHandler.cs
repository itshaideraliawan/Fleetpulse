using System.Text.Json;
using FleetPulse.Contracts.Messaging;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace FleetPulse.FleetApi.Demo;

public static class DemoControlHandler
{
    private const string ApplyScript = """
        if tonumber(redis.call('GET', KEYS[2]) or '-1') >= tonumber(ARGV[1]) then return 0 end
        local cutoff = tonumber(ARGV[3])
        if cutoff > tonumber(redis.call('GET', KEYS[3]) or '0') then
            for i = 4, #KEYS do redis.call('DEL', KEYS[i]) end
            redis.call('SET', KEYS[3], ARGV[3])
        end
        redis.call('SET', KEYS[1], ARGV[2])
        redis.call('SET', KEYS[2], ARGV[1])
        return 1
        """;

    public static async Task HandleAsync(DemoControlChanged message, FleetDbContext db,
        IConnectionMultiplexer redis, CancellationToken cancellationToken)
    {
        var keys = new List<RedisKey> { DemoControlKeys.Settings, DemoControlKeys.Revision, DemoControlKeys.ResetCutoff };
        if (message.State.ResetAtUtc is not null)
        {
            keys.AddRange([RedisFleetState.CountsKey, RedisFleetState.ExpirationsKey,
                RedisFleetState.StatusChangesKey, RedisFleetState.MessageCountKey, "fleet:status:last-push-at"]);
            var ids = await db.Devices.AsNoTracking().Select(device => device.Id).ToArrayAsync(cancellationToken);
            keys.AddRange(ids.Select(RedisFleetState.DeviceKey));
        }
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().ScriptEvaluateAsync(ApplyScript, keys.ToArray(),
            [message.State.Revision, JsonSerializer.Serialize(message.State), message.State.ResetAtUtc?.ToUnixTimeMilliseconds() ?? 0]);
    }
}
