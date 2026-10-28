using FleetPulse.Contracts.Ids;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Rollouts;

public sealed record CreateRolloutRequest(string FirmwareVersion, int? RegionId, string? Model, int? MaxDevices);

public static class RolloutEndpoints
{
    public static WebApplication MapRolloutEndpoints(this WebApplication app)
    {
        app.MapGet("/api/rollouts", async (RolloutQueries queries, CancellationToken cancellationToken) =>
                TypedResults.Ok(await queries.RecentAsync(cancellationToken)))
            .WithName("GetRecentRollouts").WithTags("Rollouts");
        app.MapGet("/api/rollouts/{id:guid}", async (Guid id, RolloutQueries queries, CancellationToken cancellationToken) =>
            {
                var result = await queries.GetAsync(new RolloutId(id), cancellationToken);
                return result is null ? Results.Problem(statusCode: 404, title: "Rollout not found") : Results.Ok(result);
            })
            .WithName("GetRolloutProgress").WithTags("Rollouts")
            .Produces<RolloutProgress>().ProducesProblem(StatusCodes.Status404NotFound);
        app.MapPost("/api/rollouts", CreateAsync).WithName("CreateRollout").WithTags("Rollouts")
            .WithSummary("Start a staged rollout for matching devices, ordered by serial number")
            .Produces<RolloutSummary>(StatusCodes.Status202Accepted).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }

    private static async Task<IResult> CreateAsync(CreateRolloutRequest request, FleetDbContext db,
        RolloutPlanner planner, RolloutQueries queries, IClock clock, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.FirmwareVersion) || request.FirmwareVersion.Length > 40)
            errors["firmwareVersion"] = ["Firmware version must be between 1 and 40 characters."];
        if (request.RegionId is <= 0) errors["regionId"] = ["Region ID must be positive."];
        if (request.Model is not null && (string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 40))
            errors["model"] = ["Model must be between 1 and 40 characters."];
        if (request.MaxDevices is < 1 or > 5000) errors["maxDevices"] = ["Device limit must be between 1 and 5000."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        // Serialize cohort selection and creation across API instances. The worker sees only committed plans.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = 'fleetpulse:rollout-create',
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
            IF @result < 0 THROW 51000, 'Could not acquire rollout creation lock.', 1;
            """, cancellationToken);
        var devices = db.Devices.AsNoTracking().AsQueryable();
        if (request.RegionId is not null) devices = devices.Where(device => device.Site.RegionId == request.RegionId);
        if (request.Model is not null) devices = devices.Where(device => device.Model == request.Model.Trim());
        var ids = await devices.OrderBy(device => device.SerialNumber).ThenBy(device => device.Id)
            .Take(request.MaxDevices ?? 5000).Select(device => device.Id).ToArrayAsync(cancellationToken);
        if (ids.Length == 0) return TypedResults.Problem(statusCode: 404, title: "No devices match the rollout target");
        if (await db.WaveTargets.AnyAsync(target => ids.Contains(target.DeviceId) &&
                target.Wave.Rollout.Status != RolloutStatus.Completed && target.Wave.Rollout.Status != RolloutStatus.RolledBack,
                cancellationToken))
            return TypedResults.Problem(statusCode: 409, title: "Devices already belong to an active rollout",
                detail: "Wait for that rollout to complete or finish rolling back before targeting these devices again.");

        var version = request.FirmwareVersion.Trim();
        var release = await db.Releases.SingleOrDefaultAsync(item => item.FirmwareVersion == version, cancellationToken);
        if (release is null)
        {
            release = new Release { Id = ReleaseId.New(), FirmwareVersion = version, CreatedAtUtc = clock.UtcNow };
            db.Releases.Add(release);
            await db.SaveChangesAsync(cancellationToken);
        }
        Rollout rollout;
        try { rollout = await planner.CreateAsync(release.Id, ids, cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Problem(statusCode: 409, title: "Fleet activity changed or reset is pending; refresh before trying again");
        }
        var progress = await queries.GetAsync(rollout.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Accepted($"/api/rollouts/{rollout.Id.Value}", progress!.Summary);
    }
}
