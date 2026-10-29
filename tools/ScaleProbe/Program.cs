using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Demo;
using FleetPulse.FleetApi.Devices;
using FleetPulse.FleetApi.Rollouts;
using FleetPulse.Simulator;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

var arguments = args.Chunk(2).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
var devices = int.Parse(arguments.GetValueOrDefault("--devices", "5000"), System.Globalization.CultureInfo.InvariantCulture);
var holdSeconds = int.Parse(arguments.GetValueOrDefault("--hold-seconds", "900"), System.Globalization.CultureInfo.InvariantCulture);
var api = new Uri(arguments["--api"]);
var gateway = new Uri(arguments["--gateway"]);
var output = Path.GetFullPath(arguments["--output"]);
if (devices != 5000 || holdSeconds < 1) throw new ArgumentException("The five-target probe requires all 5000 seeded devices and a positive hold duration.");
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(90));
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
using var http = new HttpClient { BaseAddress = api, Timeout = TimeSpan.FromMinutes(5) };
var probe = new ScaleProbe(http, gateway, devices, holdSeconds, output);
await probe.RunAsync(stop.Token);

internal sealed class ScaleProbe(HttpClient http, Uri gateway, int devices, int holdSeconds, string output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly List<ModeResult> _results = [];
    private readonly ConcurrentQueue<double> _freshness = new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var admission in new[] { true, false })
        {
            var result = new ModeResult(admission, devices, holdSeconds);
            _results.Add(result);
            try { await MeasureAsync(result, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result.Error = exception.ToString();
                Environment.ExitCode = 1;
                Console.WriteLine($"MODE FAILED: {exception.Message}");
            }
            finally { await SaveAsync(cancellationToken); }
            if (result.Error is not null) break;
        }
    }

    private async Task MeasureAsync(ModeResult result, CancellationToken cancellationToken)
    {
        var admission = result.Admission;
        Console.WriteLine($"PHASE reset admission={admission}");
        await ControlAsync(DemoAction.Reset, true, null, cancellationToken);
        await WaitDemoAsync(status => !status.ResetPending && Ready(status), TimeSpan.FromMinutes(3), cancellationToken);
        await ControlAsync(DemoAction.Settings, admission, null, cancellationToken);
        await WaitDemoAsync(Ready, TimeSpan.FromMinutes(1), cancellationToken);
        var observedAcks = new ConcurrentDictionary<FleetPulse.Contracts.Ids.CommandId, DateTimeOffset>();
        var simulator = new SimulatorRunner(new SimulatorOptions(gateway, devices, 1),
            acknowledgement => observedAcks.TryAdd(acknowledgement.CommandId, DateTimeOffset.UtcNow));
        using var simulation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var simulationTask = simulator.RunAsync(simulation.Token);
        await using var observer = new HubConnectionBuilder().WithUrl(new Uri(http.BaseAddress!, "/hubs/operator"), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
        }).Build();
        observer.On<OperatorStatusBatch>(OperatorHub.StatusBatchMethod, batch =>
        {
            var received = DateTimeOffset.UtcNow;
            foreach (var change in batch.Changes)
                if (change.Online && change.LastMessageSentAt is { } sent) _freshness.Enqueue((received - sent).TotalMilliseconds);
        });
        try
        {
            await observer.StartAsync(cancellationToken);
            var startup = Stopwatch.StartNew();
            await WaitDemoAsync(status => status.ConnectedDevices == devices && Ready(status) && simulator.GetSnapshot().Connected == devices,
                TimeSpan.FromMinutes(4), cancellationToken);
            result.StartupSeconds = startup.Elapsed.TotalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            _freshness.Clear();
            var before = simulator.GetSnapshot();
            Console.WriteLine($"PHASE hold admission={admission} seconds={holdSeconds}");
            var hold = Stopwatch.StartNew();
            while (hold.Elapsed.TotalSeconds < holdSeconds)
            {
                var status = await GetAsync<DemoStatus>("/api/demo", cancellationToken);
                result.Samples.Add(new ScaleSample(DateTimeOffset.UtcNow, simulator.GetSnapshot(), status.ConnectedDevices,
                    Process.GetCurrentProcess().WorkingSet64));
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            var after = simulator.GetSnapshot();
            result.HoldElapsedSeconds = hold.Elapsed.TotalSeconds;
            result.UnexpectedSessionClosures = after.ClosedSessions - before.ClosedSessions;
            result.HoldConnectionFailures = after.ConnectionFailures - before.ConnectionFailures;
            result.MinimumSessions = result.Samples.Min(sample => sample.Simulator.Connected);
            result.FreshnessMilliseconds = _freshness.ToArray();
            result.HeartbeatDelta = after.Heartbeats - before.Heartbeats;
            result.TelemetryDelta = after.Telemetry - before.Telemetry;
            await SaveAsync(cancellationToken);

            Console.WriteLine($"PHASE command admission={admission}");
            var commandWatch = Stopwatch.StartNew();
            using var response = await http.PostAsJsonAsync("/api/commands",
                new CreateCommandRequest(CommandKind.RunDiagnostics, null, null, null, true, 300), cancellationToken);
            response.EnsureSuccessStatusCode();
            var command = (await response.Content.ReadFromJsonAsync<CreateCommandResult>(cancellationToken))!;
            result.CommandPostSeconds = commandWatch.Elapsed.TotalSeconds;
            CommandProgress progress;
            do
            {
                progress = await GetAsync<CommandProgress>($"/api/commands/{command.CommandId}", cancellationToken);
                if (progress.Summary.Pending + progress.Summary.Acknowledged == 0) break;
                if (commandWatch.Elapsed > TimeSpan.FromMinutes(6)) throw new TimeoutException("Command did not finish.");
                await Task.Delay(250, cancellationToken);
            } while (true);
            var acknowledgements = new List<double>();
            var persisted = new List<double>();
            var missing = 0;
            for (var page = 1; page <= (int)Math.Ceiling(devices / 100d); page++)
            {
                var details = await GetAsync<CommandProgress>($"/api/commands/{command.CommandId}?page={page}&pageSize=100", cancellationToken);
                foreach (var target in details.Items)
                {
                    if (observedAcks.TryGetValue(target.Id, out var acknowledged))
                        acknowledgements.Add((acknowledged - progress.Summary.CreatedAtUtc).TotalMilliseconds);
                    else missing++;
                    if (target.AcknowledgedAtUtc is { } receipt)
                        persisted.Add((receipt - progress.Summary.CreatedAtUtc).TotalMilliseconds);
                }
            }
            result.CommandAcknowledgementsMilliseconds = acknowledgements.ToArray();
            result.PersistedReceiptMilliseconds = persisted.ToArray();
            result.MissingAcknowledgements = missing;
            result.Command = progress.Summary;
            await SaveAsync(cancellationToken);

            Console.WriteLine($"PHASE reconnect admission={admission}");
            _freshness.Clear();
            var closures = simulator.GetSnapshot().ClosedSessions;
            var reconnect = Stopwatch.StartNew();
            await ControlAsync(DemoAction.DropConnections, admission, null, cancellationToken);
            var recovered = await WaitDemoAsync(status => status.ConnectedDevices == devices && Ready(status) &&
                simulator.GetSnapshot().Connected == devices && simulator.GetSnapshot().ClosedSessions - closures >= devices,
                TimeSpan.FromMinutes(4), cancellationToken);
            result.RecoverySeconds = reconnect.Elapsed.TotalSeconds;
            result.RecoveredSessions = recovered.ConnectedDevices;
            result.StormFreshnessMilliseconds = _freshness.ToArray();
            await SaveAsync(cancellationToken);

            Console.WriteLine($"PHASE rollout admission={admission}");
            using var create = await http.PostAsJsonAsync("/api/rollouts", new CreateRolloutRequest("2.4.0", null, null, devices), cancellationToken);
            create.EnsureSuccessStatusCode();
            var rollout = (await create.Content.ReadFromJsonAsync<RolloutSummary>(cancellationToken))!;
            var rolloutWatch = Stopwatch.StartNew();
            var injected = false;
            RolloutProgress state;
            do
            {
                state = await GetAsync<RolloutProgress>($"/api/rollouts/{rollout.Id.Value}", cancellationToken);
                if (!injected && state.Waves[0].GateCheckedAtUtc is not null)
                {
                    await ControlAsync(DemoAction.Settings, admission, "2.4.0", cancellationToken);
                    result.InjectionRequestedSeconds = rolloutWatch.Elapsed.TotalSeconds;
                    injected = true;
                }
                if (state.Summary.Status is RolloutStatus.RolledBack or RolloutStatus.Completed) break;
                if (rolloutWatch.Elapsed > TimeSpan.FromMinutes(8)) throw new TimeoutException("Rollout did not finish.");
                await Task.Delay(100, cancellationToken);
            } while (true);
            result.RolloutSeconds = rolloutWatch.Elapsed.TotalSeconds;
            result.Rollout = state;
            result.FinalSimulator = simulator.GetSnapshot();
            Console.WriteLine($"MODE COMPLETE admission={admission} recovery={result.RecoverySeconds:F2}s rollout={state.Summary.Status}");
        }
        finally
        {
            simulation.Cancel();
            await simulationTask;
            await observer.StopAsync(CancellationToken.None);
        }
    }

    private async Task ControlAsync(DemoAction action, bool admission, string? badVersion, CancellationToken cancellationToken)
    {
        var state = await GetAsync<DemoStatus>("/api/demo", cancellationToken);
        using var response = await http.PostAsJsonAsync("/api/demo", new DemoRequest(Guid.NewGuid(), state.Desired.Revision,
            action, admission, badVersion, badVersion is null ? null : "Kiosk-B"), cancellationToken);
        response.EnsureSuccessStatusCode();
    }
    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) =>
        (await http.GetFromJsonAsync<T>(path, cancellationToken))!;
    private async Task<DemoStatus> WaitDemoAsync(Func<DemoStatus, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var state = await GetAsync<DemoStatus>("/api/demo", cancellationToken);
            if (predicate(state)) return state;
            if (watch.Elapsed > timeout) throw new TimeoutException($"Demo wait timed out: {JsonSerializer.Serialize(state)}");
            await Task.Delay(500, cancellationToken);
        }
    }
    private static bool Ready(DemoStatus status) => status.AppliedRevision == status.Desired.Revision && status.Gateways.Count > 0 &&
        status.Gateways.All(instance => instance.Revision == status.Desired.Revision && instance.ConfiguredDevices == instance.ConnectedDevices);
    private Task SaveAsync(CancellationToken cancellationToken) => File.WriteAllTextAsync(output,
        JsonSerializer.Serialize(new { MeasuredAtUtc = DateTimeOffset.UtcNow, Environment.MachineName, Environment.ProcessorCount,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, Modes = _results }, Json), cancellationToken);
}

internal sealed class ModeResult(bool admission, int devices, int requestedHoldSeconds)
{
    public bool Admission { get; } = admission;
    public int Devices { get; } = devices;
    public int RequestedHoldSeconds { get; } = requestedHoldSeconds;
    public double StartupSeconds { get; set; }
    public double HoldElapsedSeconds { get; set; }
    public long MinimumSessions { get; set; }
    public long UnexpectedSessionClosures { get; set; }
    public long HoldConnectionFailures { get; set; }
    public long HeartbeatDelta { get; set; }
    public long TelemetryDelta { get; set; }
    public double[] FreshnessMilliseconds { get; set; } = [];
    public double[] StormFreshnessMilliseconds { get; set; } = [];
    public double CommandPostSeconds { get; set; }
    public double[] CommandAcknowledgementsMilliseconds { get; set; } = [];
    public string AcknowledgementBasis { get; } = "Client ACK send completion minus server command creation; persisted receipt is reported separately";
    public double[] PersistedReceiptMilliseconds { get; set; } = [];
    public int MissingAcknowledgements { get; set; }
    public CommandSummary? Command { get; set; }
    public double RecoverySeconds { get; set; }
    public int RecoveredSessions { get; set; }
    public double InjectionRequestedSeconds { get; set; }
    public double RolloutSeconds { get; set; }
    public RolloutProgress? Rollout { get; set; }
    public SimulatorSnapshot? FinalSimulator { get; set; }
    public List<ScaleSample> Samples { get; } = [];
    public string? Error { get; set; }
}
internal sealed record ScaleSample(DateTimeOffset AtUtc, SimulatorSnapshot Simulator, int GatewaySessions, long ProbeWorkingSetBytes);
