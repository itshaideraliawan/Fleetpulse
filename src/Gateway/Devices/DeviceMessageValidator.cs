using System.Text;
using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;

namespace FleetPulse.Gateway.Devices;

public static class DeviceMessageValidator
{
    public const int MaxPayloadBytes = 4 * 1024;
    public const int MaxSignalRMessageBytes = 64 * 1024;

    public static bool TryValidate<TMessage>(
        JsonElement payload,
        DeviceId connectedDeviceId,
        out TMessage? message,
        out string? reason)
        where TMessage : class, IDeviceMessage
    {
        message = null;
        reason = "invalid_payload";

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaxPayloadBytes)
        {
            reason = "too_large";
            return false;
        }

        try
        {
            message = payload.Deserialize<TMessage>(JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return false;
        }

        if (message?.Header is null)
        {
            message = null;
            return false;
        }

        if (message.Header.ProtocolVersion != DeviceProtocolVersion.Current)
        {
            message = null;
            reason = "version";
            return false;
        }

        if (message.Header.DeviceId != connectedDeviceId)
        {
            message = null;
            reason = "device_id";
            return false;
        }

        reason = null;
        return true;
    }
}
