using System.Globalization;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace FleetPulse.FleetApi.Devices;

public sealed partial class StatusBroadcaster(
    IConnectionMultiplexer redis,
    IHubContext<OperatorHub> hub,
    IClock clock,
    ILogger<StatusBroadcaster> logger) : BackgroundService
{
    private const string PushGateKey = "fleet:status:last-push-at";
    private const int MaxChangesPerPush = 5_000;
    public static readonly TimeSpan PushInterval = TimeSpan.FromSeconds(1);

    private static readonly RedisValue[] StatusFields = ["online", "health", "lastSeenAt",
        "cpuPercent", "memoryPercent", "temperatureCelsius"];

    private const string AcquirePushGateScript = """
        local previous = redis.call('GET', KEYS[1])
        if previous and tonumber(ARGV[1]) - tonumber(previous) < 1000 then
            return -1
        end
        redis.call('SET', KEYS[1], ARGV[1])
        return tonumber(previous or '0')
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await FlushAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogPushFailure(logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = redis.GetDatabase();
        if (await database.SetLengthAsync(RedisFleetState.StatusChangesKey) == 0)
        {
            return;
        }

        var sentAt = clock.UtcNow;
        var nowMilliseconds = sentAt.ToUnixTimeMilliseconds();
        var previousMilliseconds = (long)await database.ScriptEvaluateAsync(AcquirePushGateScript,
            [PushGateKey], [nowMilliseconds]);
        if (previousMilliseconds == -1)
        {
            return;
        }

        var rawIds = await database.SetPopAsync(RedisFleetState.StatusChangesKey, MaxChangesPerPush);
        if (rawIds.Length == 0)
        {
            return;
        }

        var rawMessageCount = await database.StringGetSetAsync(RedisFleetState.MessageCountKey, "0");
        var messageCount = rawMessageCount.IsNull ? 0 : (long)rawMessageCount;
        try
        {
            var counts = await database.HashGetAsync(RedisFleetState.CountsKey,
                ["online", "healthy", "degraded", "unhealthy"]);
            var changes = await Task.WhenAll(rawIds.Select(async rawId =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = DeviceId.From(Guid.Parse(rawId.ToString()));
                var values = await database.HashGetAsync(RedisFleetState.DeviceKey(id), StatusFields);
                return ToChange(id, values);
            }));
            var windowSeconds = previousMilliseconds == 0 ? 1d
                : Math.Max(1d, (nowMilliseconds - previousMilliseconds) / 1000d);
            var batch = new OperatorStatusBatch(sentAt,
                ParseInt(counts[0]), ParseInt(counts[1]), ParseInt(counts[2]), ParseInt(counts[3]),
                messageCount, messageCount / windowSeconds,
                changes.OrderBy(change => change.DeviceId.Value).ToArray());
            await hub.Clients.All.SendAsync(OperatorHub.StatusBatchMethod, batch, cancellationToken);
        }
        catch
        {
            await database.SetAddAsync(RedisFleetState.StatusChangesKey, rawIds);
            if (messageCount > 0)
            {
                await database.StringIncrementAsync(RedisFleetState.MessageCountKey, messageCount);
            }

            throw;
        }
    }

    private static DeviceStatusChange ToChange(DeviceId deviceId, RedisValue[] values)
    {
        var lastSeenAt = long.TryParse(values[2].ToString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : (DateTimeOffset?)null;
        return new DeviceStatusChange(deviceId, values[0] == "1",
            values[1].IsNull ? null : values[1].ToString(), lastSeenAt,
            ParseDouble(values[3]), ParseDouble(values[4]), ParseDouble(values[5]));
    }

    private static int ParseInt(RedisValue value) =>
        int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0;

    private static double? ParseDouble(RedisValue value) =>
        double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Operator status push failed; retrying on next tick")]
    private static partial void LogPushFailure(ILogger logger, Exception exception);
}

public sealed record OperatorStatusBatch(DateTimeOffset SentAtUtc, int OnlineDevices,
    int HealthyDevices, int DegradedDevices, int UnhealthyDevices,
    long MessagesSinceLastPush, double MessagesPerSecond,
    IReadOnlyList<DeviceStatusChange> Changes);

public sealed record DeviceStatusChange(DeviceId DeviceId, bool Online, string? Health,
    DateTimeOffset? LastSeenAt, double? CpuPercent, double? MemoryPercent,
    double? TemperatureCelsius);
