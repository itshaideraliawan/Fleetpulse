using FleetPulse.Contracts.DeviceProtocol;
using FleetPulse.Contracts.Ids;

namespace FleetPulse.Simulator;

public sealed record CommandExecution(CommandResult Result, bool Replayed);

public sealed class DeviceCommandExecutor(VirtualDevice device, double failureRate)
{
    public const int CacheCapacity = 128;

    private readonly Lock _gate = new();
    private readonly Dictionary<CommandId, CacheEntry> _results = [];
    private readonly LinkedList<CommandId> _order = new();
    private long _executionCount;

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
        lock (_gate)
        {
            if (!_results.TryGetValue(command.CommandId, out entry!))
            {
                entry = new CacheEntry(
                    new TaskCompletionSource<CommandResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously),
                    _order.AddLast(command.CommandId));
                _results.Add(command.CommandId, entry);
                EvictCompleted();
                execute = true;
            }
        }

        if (execute)
        {
            try
            {
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
        Interlocked.Increment(ref _executionCount);
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 251)), cancellationToken);
        var outcome = command.Kind == CommandKind.RunDiagnostics &&
            Random.Shared.NextDouble() >= failureRate
            ? CommandOutcome.Succeeded : CommandOutcome.Failed;
        var detail = command.Kind != CommandKind.RunDiagnostics
            ? "Unsupported command kind."
            : outcome == CommandOutcome.Succeeded ? "Diagnostics passed." : "Diagnostics failed.";
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
