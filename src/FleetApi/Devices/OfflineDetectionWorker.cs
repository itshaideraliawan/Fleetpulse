namespace FleetPulse.FleetApi.Devices;

public sealed partial class OfflineDetectionWorker(RedisFleetState state, ILogger<OfflineDetectionWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ScanAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    LogScanFailure(logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        while (await state.MarkExpiredAsync(cancellationToken) == RedisFleetState.ExpirationBatchSize)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Fleet offline scan failed; retrying on next tick")]
    private static partial void LogScanFailure(ILogger logger, Exception exception);
}
