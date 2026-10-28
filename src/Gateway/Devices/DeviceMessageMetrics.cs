using System.Diagnostics.Metrics;

namespace FleetPulse.Gateway.Devices;

public static class DeviceMessageMetrics
{
    public const string MeterName = "FleetPulse.Gateway.DeviceMessages";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Accepted = Meter.CreateCounter<long>("gateway.device_messages_accepted");
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>("gateway.device_messages_dropped");
    private static readonly Counter<long> Admissions = Meter.CreateCounter<long>("gateway.device_connection_attempts");

    public static void RecordAdmission(bool allowed) => Admissions.Add(1,
        new KeyValuePair<string, object?>("outcome", allowed ? "admitted" : "rejected"));

    public static void RecordAccepted(string kind) => Accepted.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public static void RecordDropped(string kind, string reason) => Dropped.Add(1,
        new KeyValuePair<string, object?>("kind", kind),
        new KeyValuePair<string, object?>("reason", reason));
}
