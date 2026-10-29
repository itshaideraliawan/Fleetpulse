using System.Security.Claims;
using FleetPulse.Gateway.Devices;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace FleetPulse.UnitTests;

public sealed class DeviceConnectionRegistryTests
{
    [Fact]
    public void RetryTargetsExcludeAcknowledgedDevicesAndIncludeNewConnections()
    {
        var registry = new DeviceConnectionRegistry();
        registry.Add(new Connection("ready"));
        registry.Add(new Connection("waiting"));
        registry.Acknowledge("ready", 1);
        Assert.Equal("waiting", Assert.Single(registry.UnconfiguredConnectionIds(1)));
        registry.Add(new Connection("new"));
        Assert.Equal(["new", "waiting"], registry.UnconfiguredConnectionIds(1).Order());
        Assert.Equal(3, registry.UnconfiguredConnectionIds(2).Length);
        registry.Remove("waiting");
        Assert.Equal("new", Assert.Single(registry.UnconfiguredConnectionIds(1)));
    }

    private sealed class Connection(string id) : HubCallerContext
    {
        public override string ConnectionId => id;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
