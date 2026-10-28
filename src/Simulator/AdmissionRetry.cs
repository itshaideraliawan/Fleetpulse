using System.Globalization;
using System.Net;
using System.Net.WebSockets;

namespace FleetPulse.Simulator;

public static class AdmissionRetry
{
    public static TimeSpan ParseDelay(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and <= 86400
            ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;

    public static async ValueTask<WebSocket> ConnectAsync(Uri uri, Action<TimeSpan> onRejected,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            return socket;
        }
        catch
        {
            if (socket.HttpStatusCode == HttpStatusCode.TooManyRequests &&
                socket.HttpResponseHeaders?.TryGetValue("Retry-After", out var values) == true)
            {
                onRejected(ParseDelay(values.FirstOrDefault()));
            }
            socket.Dispose();
            throw;
        }
    }
}
