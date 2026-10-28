using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Messaging;

namespace FleetPulse.Simulator;

public sealed record CommandExecution(CommandResult Result, bool Replayed);

public sealed class DeviceCommandExecutor(VirtualDevice device, double failureRate,
    string? badReleaseVersion = null, string? badReleaseModel = null)
{
    public const int CacheCapacity = 128;

    private readonly Lock _gate = new();
    private readonly Dictionary<CommandId, CacheEntry> _results = [];
    private readonly LinkedList<CommandId> _order = new();
    private long _executionCount;
    private Task _lastExecution = Task.CompletedTask;
    private DemoControlSnapshot? _demo;

    public async Task ApplyDemoAsync(DemoControlSnapshot state, CancellationToken cancellationToken)
    {
        Task predecessor;
        TaskCompletionSource completion;
        lock (_gate)
        {
            var previous = _demo;
            if (previous is not null && state.Revision <= previous.Revision) return;
            Volatile.Write(ref _demo, state);
            if (state.ResetGeneration <= (previous?.ResetGeneration ?? 0)) return;
            predecessor = _lastExecution;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _lastExecution = completion.Task;
        }
        try
        {
            await predecessor.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellationToken.ThrowIfCancellationRequested();
            device.SetFirmwareVersion("1.0.0");
        }
        finally { completion.TrySetResult(); }
    }

    public long ExecutionCount => Interlocked.Read(ref _executionCount);

    public async Task<CommandExecution> ExecuteAsync(DeviceCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Header.DeviceId != device.Id ||
            command.Header.ProtocolVersion != DeviceProtocolVersion.Current ||
            command.CommandId.Value == Guid.Empty)
        {
            throw new ArgumentException("Command does not match this device or protocol.",
                nameof(command));
        }

        CacheEntry entry;
        var execute = false;
        var predecessor = Task.CompletedTask;
        lock (_gate)
        {
            if (!_results.TryGetValue(command.CommandId, out entry!))
            {
                entry = new CacheEntry(
                    new TaskCompletionSource<CommandResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously),
                    _order.AddLast(command.CommandId));
                _results.Add(command.CommandId, entry);
                predecessor = _lastExecution;
                _lastExecution = entry.Completion.Task;
                EvictCompleted();
                execute = true;
            }
        }

        if (execute)
        {
            try
            {
                Interlocked.Increment(ref _executionCount);
                // Keep queue order even if this request is cancelled while its predecessor is running.
                await predecessor.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                var result = await ExecuteNewAsync(command, cancellationToken);
                entry.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RemoveFailed(command.CommandId, entry);
                entry.Completion.TrySetCanceled(cancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                RemoveFailed(command.CommandId, entry);
                entry.Completion.TrySetException(exception);
                throw;
            }
        }

        return new CommandExecution(await entry.Completion.Task.WaitAsync(cancellationToken), !execute);
    }

    private async Task<CommandResult> ExecuteNewAsync(DeviceCommand command,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 251)), cancellationToken);
        var demo = Volatile.Read(ref _demo);
        if (demo?.ResetAtUtc is { } resetAt && command.Header.SentAtUtc <= resetAt)
            return Result(command, CommandOutcome.Failed, "Command predates fleet reset.");
        if (command.ExpiresAtUtc <= DateTimeOffset.UtcNow) return Result(command, CommandOutcome.Failed, "Command expired before execution.");
        if (command.Kind == CommandKind.RunDiagnostics)
        {
            var outcome = Random.Shared.NextDouble() >= failureRate ? CommandOutcome.Succeeded : CommandOutcome.Failed;
            return Result(command, outcome, outcome == CommandOutcome.Succeeded ? "Diagnostics passed." : "Diagnostics failed.");
        }
        if (command.Kind is not (CommandKind.InstallFirmware or CommandKind.RollbackFirmware))
            return Result(command, CommandOutcome.Failed, "Unsupported command kind.");
        if (command.ReleaseId is null || command.ReleaseId.Value.Value == Guid.Empty ||
            command.RolloutId is null || command.RolloutId.Value.Value == Guid.Empty ||
            command.WaveId is null || command.WaveId.Value.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.FirmwareVersion) || command.FirmwareVersion.Length > 40)
            return Result(command, CommandOutcome.Failed, "Firmware command requires release, rollout, wave, and a version of at most 40 characters.");

        if (command.Kind == CommandKind.InstallFirmware &&
            string.Equals(command.FirmwareVersion, demo is null || demo.Revision == 0 ? badReleaseVersion : demo.BadReleaseVersion, StringComparison.Ordinal) &&
            string.Equals(device.Model, demo is null || demo.Revision == 0 ? badReleaseModel : demo.BadReleaseModel, StringComparison.Ordinal))
            return Result(command, CommandOutcome.Failed, $"Firmware {command.FirmwareVersion} is incompatible with model {device.Model}.");

        device.SetFirmwareVersion(command.FirmwareVersion);
        return Result(command, CommandOutcome.Succeeded, command.Kind == CommandKind.InstallFirmware
            ? $"Installed firmware {command.FirmwareVersion}." : $"Restored firmware {command.FirmwareVersion}.");
    }

    private CommandResult Result(DeviceCommand command, CommandOutcome outcome, string detail)
    {
        return new CommandResult(
            new DeviceMessageHeader(DeviceProtocolVersion.Current, device.Id, DateTimeOffset.UtcNow),
            command.CommandId, outcome, detail);
    }

    private void EvictCompleted()
    {
        while (_results.Count > CacheCapacity && _order.First is { } oldest &&
            _results[oldest.Value].Completion.Task.IsCompleted)
        {
            _order.RemoveFirst();
            _results.Remove(oldest.Value);
        }
    }

    private void RemoveFailed(CommandId commandId, CacheEntry entry)
    {
        lock (_gate)
        {
            if (_results.TryGetValue(commandId, out var cached) && ReferenceEquals(cached, entry))
            {
                _results.Remove(commandId);
                _order.Remove(entry.Node);
            }
        }
    }

    private sealed record CacheEntry(
        TaskCompletionSource<CommandResult> Completion,
        LinkedListNode<CommandId> Node);
}
