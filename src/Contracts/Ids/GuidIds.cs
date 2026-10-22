using System.Text.Json.Serialization;

namespace FleetPulse.Contracts.Ids;

public interface IGuidId<TSelf> where TSelf : IGuidId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}

[JsonConverter(typeof(GuidIdJsonConverter<DeviceId>))]
public readonly record struct DeviceId(Guid Value) : IGuidId<DeviceId>
{
    public static DeviceId From(Guid value) => new(value);

    public static DeviceId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

[JsonConverter(typeof(GuidIdJsonConverter<CommandId>))]
public readonly record struct CommandId(Guid Value) : IGuidId<CommandId>
{
    public static CommandId From(Guid value) => new(value);

    public static CommandId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

[JsonConverter(typeof(GuidIdJsonConverter<RolloutId>))]
public readonly record struct RolloutId(Guid Value) : IGuidId<RolloutId>
{
    public static RolloutId From(Guid value) => new(value);

    public static RolloutId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

[JsonConverter(typeof(GuidIdJsonConverter<ReleaseId>))]
public readonly record struct ReleaseId(Guid Value) : IGuidId<ReleaseId>
{
    public static ReleaseId From(Guid value) => new(value);

    public static ReleaseId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

[JsonConverter(typeof(GuidIdJsonConverter<WaveId>))]
public readonly record struct WaveId(Guid Value) : IGuidId<WaveId>
{
    public static WaveId From(Guid value) => new(value);

    public static WaveId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
