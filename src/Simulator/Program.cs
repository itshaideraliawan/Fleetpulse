using FleetPulse.Simulator;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine(SimulatorOptions.Usage);
    return;
}

if (!SimulatorOptions.TryParse(args, Environment.GetEnvironmentVariable("GATEWAY_URL"),
        out var options, out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(SimulatorOptions.Usage);
    Environment.ExitCode = 1;
    return;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

await new SimulatorRunner(options!).RunAsync(shutdown.Token);
