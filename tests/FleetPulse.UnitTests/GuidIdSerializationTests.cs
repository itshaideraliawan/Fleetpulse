using System.Text.Json;
using FleetPulse.Contracts.Ids;

namespace FleetPulse.UnitTests;

public sealed class GuidIdSerializationTests
{
    [Fact]
    public void DeviceIdRoundTripsAsJsonString()
    {
        var id = DeviceId.New();

        var json = JsonSerializer.Serialize(id);
        var restored = JsonSerializer.Deserialize<DeviceId>(json);

        Assert.Equal($"\"{id.Value:D}\"", json);
        Assert.Equal(id, restored);
    }

    [Fact]
    public void CommandIdRejectsInvalidGuid()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CommandId>("\"invalid\""));
    }
}
