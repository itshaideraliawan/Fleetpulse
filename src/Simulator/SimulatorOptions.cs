using System.Globalization;

namespace FleetPulse.Simulator;

public sealed record SimulatorOptions(Uri Gateway, int Devices, int Start, double CommandFailureRate = 0,
    string? BadReleaseVersion = null, string? BadReleaseModel = null)
{
    public const string Usage = "Usage: dotnet run --project src/Simulator -- --gateway http://localhost:<gateway-port> [--devices 1000] [--start 1] [--command-failure-rate 0.05] [--bad-release-version 2.4.0 --bad-release-model Kiosk-B]";

    public static bool TryParse(string[] args, string? gatewayEnvironment,
        out SimulatorOptions? options, out string? error)
    {
        options = null;
        error = null;
        var gatewayText = gatewayEnvironment;
        var devices = 1000;
        var start = 1;
        var commandFailureRate = 0.0;
        string? badReleaseVersion = null;
        string? badReleaseModel = null;

        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Missing value for {args[index]}.";
                return false;
            }

            switch (args[index])
            {
                case "--bad-release-version":
                    badReleaseVersion = args[index + 1];
                    break;
                case "--bad-release-model":
                    badReleaseModel = args[index + 1];
                    break;
                case "--gateway":
                    gatewayText = args[index + 1];
                    break;
                case "--devices" when int.TryParse(args[index + 1], out var count):
                    devices = count;
                    break;
                case "--start" when int.TryParse(args[index + 1], out var first):
                    start = first;
                    break;
                case "--command-failure-rate" when double.TryParse(args[index + 1],
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var rate):
                    commandFailureRate = rate;
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

        if (!double.IsFinite(commandFailureRate) || commandFailureRate is < 0 or > 1)
        {
            error = "Command failure rate must be between 0 and 1.";
            return false;
        }

        if ((badReleaseVersion is null) != (badReleaseModel is null) ||
            badReleaseVersion is not null && (string.IsNullOrWhiteSpace(badReleaseVersion) || badReleaseVersion.Length > 40) ||
            badReleaseModel is not null && !VirtualDevice.IsKnownModel(badReleaseModel))
        {
            error = "Supply both --bad-release-version (1 to 40 characters) and --bad-release-model (Kiosk-A, Kiosk-B, or Kiosk-C).";
            return false;
        }

        options = new SimulatorOptions(gateway, devices, start, commandFailureRate, badReleaseVersion, badReleaseModel);
        return true;
    }
}
