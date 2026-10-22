using System.Text.Json;
using System.Text.Json.Serialization;

namespace FleetPulse.Contracts.Ids;

public sealed class GuidIdJsonConverter<TId> : JsonConverter<TId>
    where TId : IGuidId<TId>
{
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => TId.From(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
