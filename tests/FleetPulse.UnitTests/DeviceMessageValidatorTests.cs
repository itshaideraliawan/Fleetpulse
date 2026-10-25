using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Gateway.Devices;

namespace FleetPulse.UnitTests;

public sealed class DeviceMessageValidatorTests
{
    [Fact]
    public void AcceptsCurrentVersionForConnectedDevice()
    {
        var deviceId = DeviceId.New();
        var payload = Payload(deviceId, DeviceProtocolVersion.Current);

        var accepted = DeviceMessageValidator.TryValidate<Heartbeat>(payload, deviceId, out var message, out var reason);

        Assert.True(accepted);
        Assert.NotNull(message);
        Assert.Null(reason);
    }

    [Fact]
    public void DropsUnsupportedVersion()
    {
        var deviceId = DeviceId.New();

        var accepted = DeviceMessageValidator.TryValidate<Heartbeat>(Payload(deviceId, 99), deviceId, out _, out var reason);

        Assert.False(accepted);
        Assert.Equal("version", reason);
    }

    [Fact]
    public void DropsDifferentDeviceId()
    {
        var deviceId = DeviceId.New();

        var accepted = DeviceMessageValidator.TryValidate<Heartbeat>(Payload(DeviceId.New(), 1), deviceId, out _, out var reason);

        Assert.False(accepted);
        Assert.Equal("device_id", reason);
    }

    [Fact]
    public void CountsUnknownFieldsTowardSizeLimit()
    {
        var deviceId = DeviceId.New();
        var json = JsonSerializer.Serialize(new Heartbeat(
            new DeviceMessageHeader(1, deviceId, DateTimeOffset.UnixEpoch), DeviceHealth.Healthy), JsonSerializerOptions.Web);
        var padded = json[..^1] + ",\"padding\":\"" + new string('x', DeviceMessageValidator.MaxPayloadBytes) + "\"}";
        using var document = JsonDocument.Parse(padded);

        var accepted = DeviceMessageValidator.TryValidate<Heartbeat>(document.RootElement, deviceId, out _, out var reason);

        Assert.False(accepted);
        Assert.Equal("too_large", reason);
    }

    private static JsonElement Payload(DeviceId deviceId, int version) => JsonSerializer.SerializeToElement(
        new Heartbeat(new DeviceMessageHeader(version, deviceId, DateTimeOffset.UnixEpoch), DeviceHealth.Healthy),
        JsonSerializerOptions.Web);
}
