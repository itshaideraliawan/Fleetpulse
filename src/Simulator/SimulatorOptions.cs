namespace FleetPulse.Simulator;

public sealed record SimulatorOptions(Uri Gateway, int Devices, int Start)
{
    public const string Usage = "Usage: dotnet run --project src/Simulator -- --gateway http://localhost:<gateway-port> [--devices 1000] [--start 1]";

    public static bool TryParse(string[] args, string? gatewayEnvironment,
        out SimulatorOptions? options, out string? error)
    {
        options = null;
        error = null;
        var gatewayText = gatewayEnvironment;
        var devices = 1000;
        var start = 1;

        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Missing value for {args[index]}.";
                return false;
            }

            switch (args[index])
            {
                case "--gateway":
                    gatewayText = args[index + 1];
                    break;
                case "--devices" when int.TryParse(args[index + 1], out var count):
                    devices = count;
                    break;
                case "--start" when int.TryParse(args[index + 1], out var first):
                    start = first;
                    break;
                default:
                    error = $"Invalid option or value: {args[index]}.";
                    return false;
            }
        }

        if (!Uri.TryCreate(gatewayText, UriKind.Absolute, out var gateway) ||
            (gateway.Scheme != Uri.UriSchemeHttp && gateway.Scheme != Uri.UriSchemeHttps))
        {
            error = "Supply an HTTP(S) Gateway URL with --gateway or GATEWAY_URL.";
            return false;
        }

        if (devices < 1 || start < 1 || (long)start + devices - 1 > 5000)
        {
            error = "The device range must be within seeded devices 1 through 5000.";
            return false;
        }

        options = new SimulatorOptions(gateway, devices, start);
        return true;
    }
}
