using System.Text.Json;
using FleetPulse.Contracts.Messaging;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Devices;
using FleetPulse.FleetApi.Rollouts;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Wolverine.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace FleetPulse.FleetApi.Demo;

public enum DemoAction { Settings, DropConnections, Reset }
public sealed record DemoRequest(Guid RequestId, long ExpectedRevision, DemoAction Action,
    bool AdmissionEnabled, string? BadReleaseVersion, string? BadReleaseModel);
public sealed record DemoStatus(DemoControlSnapshot Desired, long AppliedRevision,
    DateTimeOffset SampledAtUtc, DateTimeOffset? DropAtUtc, int RecoveryTarget,
    int ConnectedDevices, bool ResetPending, IReadOnlyList<GatewayDemoStatus> Gateways);

public sealed class DemoQueries(FleetDbContext db, IConnectionMultiplexer redis, IClock clock)
{
    public async Task<DemoStatus> GetAsync(CancellationToken cancellationToken)
    {
        var state = await db.DemoControls.AsNoTracking().SingleAsync(cancellationToken);
        var database = redis.GetDatabase();
        var now = clock.UtcNow;
        var instances = await database.SortedSetRangeByScoreAsync(DemoControlKeys.Gateways,
            now.AddSeconds(-5).ToUnixTimeMilliseconds());
        var values = instances.Length == 0 ? [] : await database.StringGetAsync(
            instances.Select(instance => (RedisKey)DemoControlKeys.Gateway(instance.ToString())).ToArray());
        var gateways = values.Where(value => !value.IsNull)
            .Select(value => JsonSerializer.Deserialize<GatewayDemoStatus>(value.ToString())!).ToArray();
        var revision = await database.StringGetAsync(DemoControlKeys.Revision);
        return new DemoStatus(state.Snapshot(), revision.IsNull ? -1 : (long)revision,
            now, state.DropAtUtc, state.RecoveryTarget, gateways.Sum(gateway => gateway.ConnectedDevices), state.ResetPending, gateways);
    }
}

public static class DemoEndpoints
{
    public static WebApplication MapDemoEndpoints(this WebApplication app)
    {
        app.MapGet("/api/demo", async (DemoQueries queries, CancellationToken cancellationToken) =>
                TypedResults.Ok(await queries.GetAsync(cancellationToken)))
            .WithName("GetDemoStatus").WithTags("Demo");
        app.MapPost("/api/demo", ApplyAsync).WithName("ApplyDemoControl").WithTags("Demo")
            .Produces<DemoStatus>(StatusCodes.Status202Accepted).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }

    private static async Task<IResult> ApplyAsync(DemoRequest request, [FromServices] IDbContextOutbox<FleetDbContext> outbox,
        DemoQueries queries, IClock clock, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.RequestId == Guid.Empty) errors["requestId"] = ["Request ID must not be empty."];
        if (request.ExpectedRevision < 0) errors["expectedRevision"] = ["Revision must not be negative."];
        if (!Enum.IsDefined(request.Action)) errors["action"] = ["Unknown demo action."];
        if ((request.BadReleaseVersion is null) != (request.BadReleaseModel is null) ||
            request.BadReleaseVersion is not null && (string.IsNullOrWhiteSpace(request.BadReleaseVersion) || request.BadReleaseVersion.Length > 40) ||
            request.BadReleaseModel is not null && request.BadReleaseModel is not ("Kiosk-A" or "Kiosk-B" or "Kiosk-C"))
            errors["badRelease"] = ["Choose a version of 1–40 characters and model Kiosk-A, Kiosk-B, or Kiosk-C, or clear both."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var db = outbox.DbContext;
        var state = await db.DemoControls.SingleAsync(cancellationToken);
        if (state.LastRequestId == request.RequestId) return TypedResults.Accepted("/api/demo", await queries.GetAsync(cancellationToken));
        if (state.Revision != request.ExpectedRevision)
            return TypedResults.Problem(statusCode: 409, title: "Demo settings changed; refresh before trying again");
        if (state.ResetPending) return TypedResults.Problem(statusCode: 409, title: "Fleet reset is still being applied");
        if (request.Action == DemoAction.Reset)
        {
            if (await db.Rollouts.AnyAsync(rollout => rollout.Status != RolloutStatus.Completed && rollout.Status != RolloutStatus.RolledBack, cancellationToken) ||
                await db.CommandTargets.AnyAsync(target => target.Status == CommandTargetStatus.Pending || target.Status == CommandTargetStatus.Acknowledged, cancellationToken))
                return TypedResults.Problem(statusCode: 409, title: "Wait for commands and rollouts to finish before resetting");

            // Tracked changes and the control message are saved together by the outbox.
            db.RolloutTransitions.RemoveRange(await db.RolloutTransitions.ToArrayAsync(cancellationToken));
            db.WaveTargets.RemoveRange(await db.WaveTargets.ToArrayAsync(cancellationToken));
            db.Waves.RemoveRange(await db.Waves.ToArrayAsync(cancellationToken));
            db.Rollouts.RemoveRange(await db.Rollouts.ToArrayAsync(cancellationToken));
            db.Releases.RemoveRange(await db.Releases.ToArrayAsync(cancellationToken));
            db.CommandTargets.RemoveRange(await db.CommandTargets.ToArrayAsync(cancellationToken));
            db.Commands.RemoveRange(await db.Commands.ToArrayAsync(cancellationToken));
            foreach (var device in await db.Devices.ToArrayAsync(cancellationToken)) device.FirmwareVersion = "1.0.0";
            state.ResetGeneration++;
            state.ResetPending = true;
            state.ResetAtUtc = clock.UtcNow;
            state.BadReleaseVersion = null;
            state.BadReleaseModel = null;
            state.AdmissionEnabled = true;
            state.DropAtUtc = null;
            state.RecoveryTarget = 0;
        }
        else if (request.Action == DemoAction.DropConnections)
        {
            var current = await queries.GetAsync(cancellationToken);
            if (current.Gateways.Count == 0) return TypedResults.Problem(statusCode: 409, title: "No active Gateway has reported yet");
            state.DropGeneration++;
            state.DropAtUtc = clock.UtcNow;
            state.RecoveryTarget = current.ConnectedDevices;
        }
        else
        {
            state.AdmissionEnabled = request.AdmissionEnabled;
            state.BadReleaseVersion = request.BadReleaseVersion?.Trim();
            state.BadReleaseModel = request.BadReleaseModel;
        }
        state.Revision++;
        state.WorkVersion++;
        state.LastRequestId = request.RequestId;
        await outbox.PublishAsync(new DemoControlChanged(state.Snapshot()));
        try { await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            return TypedResults.Problem(statusCode: 409, title: "Fleet activity changed; refresh before trying again");
        }
        return TypedResults.Accepted("/api/demo", await queries.GetAsync(cancellationToken));
    }
}
