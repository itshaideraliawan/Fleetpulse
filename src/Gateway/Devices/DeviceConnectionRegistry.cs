using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace FleetPulse.Gateway.Devices;

public sealed class DeviceConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    public int Count => _connections.Count;
    public string[] ConnectionIds => _connections.Keys.ToArray();
    public void Add(HubCallerContext context) => _connections[context.ConnectionId] = new Connection(context);
    public void Remove(string id) => _connections.TryRemove(id, out _);
    public void Acknowledge(string id, long revision)
    {
        if (_connections.TryGetValue(id, out var connection)) Interlocked.Exchange(ref connection.Revision, revision);
    }
    public int Configured(long revision) => _connections.Values.Count(connection => Interlocked.Read(ref connection.Revision) == revision);
    public void AbortAll()
    {
        foreach (var connection in _connections.Values) connection.Context.Abort();
    }
    private sealed class Connection(HubCallerContext context)
    {
        public HubCallerContext Context { get; } = context;
        public long Revision = -1;
    }
}
