using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Demo;

public sealed partial class DemoResetCompletionWorker(IServiceScopeFactory scopes,
    ILogger<DemoResetCompletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await CompleteAsync(stoppingToken); }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { LogFailure(logger, exception); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
        if (!await db.DemoControls.AnyAsync(state => state.ResetPending, cancellationToken)) return;
        var status = await scope.ServiceProvider.GetRequiredService<DemoQueries>().GetAsync(cancellationToken);
        if (status.AppliedRevision != status.Desired.Revision || status.Gateways.Count == 0 ||
            status.Gateways.Any(gateway => gateway.Revision != status.Desired.Revision || gateway.ConfiguredDevices != gateway.ConnectedDevices)) return;
        await db.DemoControls.Where(state => state.Revision == status.Desired.Revision && state.ResetPending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(state => state.ResetPending, false), cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Demo reset completion check failed")]
    private static partial void LogFailure(ILogger logger, Exception exception);
}
