using Microsoft.AspNetCore.SignalR;

namespace FleetPulse.FleetApi.Devices;

public sealed class OperatorHub : Hub
{
    public const string Route = "/hubs/operator";
    public const string StatusBatchMethod = "statusBatch";
}
