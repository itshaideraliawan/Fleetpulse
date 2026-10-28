using FleetPulse.Contracts.DeviceProtocol;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace FleetPulse.Simulator;

public sealed class SimulatorRunner(SimulatorOptions options)
{
    private long _connected;
    private long _heartbeats;
    private long _telemetry;
    private long _failedConnections;
    private long _commandsExecuted;
    private long _commandsReplayed;
    private long _commandsFailed;
    private long _firmwareInstalled;
    private long _firmwareRolledBack;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"Starting {options.Devices} devices at sequence {options.Start}; Gateway={options.Gateway}");
        var devices = Enumerable.Range(options.Start, options.Devices)
            .Select(sequence => RunDeviceAsync(VirtualDevice.Create(sequence), cancellationToken))
            .ToArray();

        var reporter = ReportAsync(cancellationToken);
        await Task.WhenAll(devices);
        await reporter;
    }

    private async Task RunDeviceAsync(VirtualDevice device, CancellationToken cancellationToken)
    {
        var executor = new DeviceCommandExecutor(device, options.CommandFailureRate,
            options.BadReleaseVersion, options.BadReleaseModel);
        try
        {
            var startupSpread = Math.Min(20_000, options.Devices * 4);
            await Task.Delay(Random.Shared.Next(startupSpread), cancellationToken);
            var failureCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var hubUrl = new Uri($"{options.Gateway.GetLeftPart(UriPartial.Authority)}/hubs/device?deviceId={device.Id}");
                var retryAfter = TimeSpan.Zero;
                await using var connection = new HubConnectionBuilder()
                    .WithUrl(hubUrl, transport =>
                    {
                        transport.Transports = HttpTransportType.WebSockets;
                        transport.SkipNegotiation = true;
                        transport.WebSocketFactory = (context, token) =>
                            AdmissionRetry.ConnectAsync(context.Uri, delay => retryAfter = delay, token);
                    })
                    .Build();
                connection.On<DeviceCommand>("ReceiveCommand", command =>
                    HandleCommandAsync(connection, device, executor, command, cancellationToken));

                try
                {
                    await connection.StartAsync(cancellationToken);
                    Interlocked.Increment(ref _connected);
                    failureCount = 0;
                    try
                    {
                        await SendLoopAsync(connection, device, cancellationToken);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _connected);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _failedConnections);
                    if (failureCount == 0 && device.Sequence % 100 == 1)
                    {
                        Console.Error.WriteLine($"DeviceId={device.Id} connection failed: {exception.Message}");
                    }
                }

                failureCount++;
                var delay = retryAfter + FullJitterBackoff.NextDelay(failureCount, Random.Shared.NextDouble());
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C ends each virtual device.
        }
    }

    private async Task HandleCommandAsync(HubConnection connection, VirtualDevice device,
        DeviceCommandExecutor executor, DeviceCommand command, CancellationToken cancellationToken)
    {
        if (command.Header.DeviceId != device.Id ||
            command.Header.ProtocolVersion != DeviceProtocolVersion.Current ||
            command.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return;
        }

        try
        {
            var ack = new CommandAck(
                new DeviceMessageHeader(DeviceProtocolVersion.Current, device.Id, DateTimeOffset.UtcNow),
                command.CommandId);
            await connection.SendCoreAsync("SendCommandAck", [ack], cancellationToken);
            var execution = await executor.ExecuteAsync(command, cancellationToken);
            if (execution.Replayed)
            {
                Interlocked.Increment(ref _commandsReplayed);
            }
            else
            {
                Interlocked.Increment(ref _commandsExecuted);
                if (execution.Result.Outcome == CommandOutcome.Failed)
                {
                    Interlocked.Increment(ref _commandsFailed);
                }
                else if (command.Kind == CommandKind.InstallFirmware) Interlocked.Increment(ref _firmwareInstalled);
                else if (command.Kind == CommandKind.RollbackFirmware) Interlocked.Increment(ref _firmwareRolledBack);
            }

            await connection.SendCoreAsync("SendCommandResult", [execution.Result], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The simulator is stopping.
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"DeviceId={device.Id} CommandId={command.CommandId} RolloutId={command.RolloutId} " +
                $"report failed: {exception.Message}");
        }
    }

    private async Task SendLoopAsync(HubConnection connection, VirtualDevice device, CancellationToken cancellationToken)
    {
        var nextHeartbeat = DateTimeOffset.MinValue;
        var nextTelemetry = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (connection.State != HubConnectionState.Connected)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= nextHeartbeat)
            {
                await connection.SendCoreAsync("SendHeartbeat", [device.CreateHeartbeat(now)], cancellationToken);
                Interlocked.Increment(ref _heartbeats);
                nextHeartbeat = now.AddSeconds(15);
            }

            if (now >= nextTelemetry)
            {
                await connection.SendCoreAsync("SendTelemetry", [device.CreateTelemetry(now)], cancellationToken);
                Interlocked.Increment(ref _telemetry);
                nextTelemetry = now.AddSeconds(10);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task ReportAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Console.WriteLine($"Connected={Interlocked.Read(ref _connected)}/{options.Devices} " +
                    $"Heartbeats={Interlocked.Read(ref _heartbeats)} Telemetry={Interlocked.Read(ref _telemetry)} " +
                    $"ConnectionFailures={Interlocked.Read(ref _failedConnections)} " +
                    $"CommandsExecuted={Interlocked.Read(ref _commandsExecuted)} " +
                    $"CommandsReplayed={Interlocked.Read(ref _commandsReplayed)} " +
                    $"CommandsFailed={Interlocked.Read(ref _commandsFailed)} " +
                    $"FirmwareInstalled={Interlocked.Read(ref _firmwareInstalled)} " +
                    $"FirmwareRolledBack={Interlocked.Read(ref _firmwareRolledBack)}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The process is stopping.
        }
    }
}
