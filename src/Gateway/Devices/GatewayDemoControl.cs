using System.Text.Json;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace FleetPulse.Gateway.Devices;

public sealed partial class GatewayDemoControl(IConnectionMultiplexer redis, DeviceConnectionRegistry connections,
    ConnectionAdmissionControl admission, IHubContext<DeviceHub> hub, IClock clock,
    ILogger<GatewayDemoControl> logger) : BackgroundService
{
    public const string ClientMethod = "DemoControl";
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private DemoControlSnapshot? _current;
    public DemoControlSnapshot? Current => Volatile.Read(ref _current);
    private static RedisKey ReadyKey(string connectionId) => $"fleetpulse:demo:ready:{connectionId}";
    public async Task<bool> IsReadyAsync(string connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Current;
        if (current is null) return true;
        var value = await redis.GetDatabase().StringGetAsync(ReadyKey(connectionId));
        return !value.IsNull && (long)value == current.Revision;
    }
    public async Task AcknowledgeAsync(string connectionId, long revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().StringSetAsync(ReadyKey(connectionId), revision, TimeSpan.FromMinutes(5));
        connections.Acknowledge(connectionId, revision);
    }
    public async Task DisconnectAsync(string connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await redis.GetDatabase().KeyDeleteAsync(ReadyKey(connectionId));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try { await ApplyAsync(stoppingToken); }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { LogFailure(logger, exception); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var db = redis.GetDatabase();
        var raw = await db.StringGetAsync(DemoControlKeys.Settings);
        if (!raw.IsNull)
        {
            var next = JsonSerializer.Deserialize<DemoControlSnapshot>(raw.ToString())!;
            var previous = Current;
            if (previous is null || next.Revision > previous.Revision)
            {
                admission.SetDemoEnabled(next.AdmissionEnabled);
                Volatile.Write(ref _current, next);
                try { await hub.Clients.Clients(connections.ConnectionIds).SendAsync(ClientMethod, next, cancellationToken); }
                catch { Volatile.Write(ref _current, previous); throw; }
                if (next.DropGeneration > (previous?.DropGeneration ?? 0) || next.ResetGeneration > (previous?.ResetGeneration ?? 0))
                    connections.AbortAll();
            }
            else if (connections.Configured(next.Revision) < connections.Count)
                await hub.Clients.Clients(connections.UnconfiguredConnectionIds(next.Revision))
                    .SendAsync(ClientMethod, next, cancellationToken);
        }
        var current = Current;
        var now = clock.UtcNow;
        var status = new GatewayDemoStatus(_instanceId, current?.Revision ?? -1, connections.Count,
            connections.Configured(current?.Revision ?? -1), now, current?.AdmissionEnabled ?? admission.Enabled);
        await db.StringSetAsync(DemoControlKeys.Gateway(_instanceId), JsonSerializer.Serialize(status), TimeSpan.FromSeconds(5));
        await db.SortedSetAddAsync(DemoControlKeys.Gateways, _instanceId, now.ToUnixTimeMilliseconds());
        await db.SortedSetRemoveRangeByScoreAsync(DemoControlKeys.Gateways, double.NegativeInfinity, now.AddMinutes(-1).ToUnixTimeMilliseconds());
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Gateway demo control synchronization failed")]
    private static partial void LogFailure(ILogger logger, Exception exception);
}
