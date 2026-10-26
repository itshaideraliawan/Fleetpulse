using FleetPulse.Contracts.Ids;

namespace FleetPulse.FleetApi.Devices;

public static class DeviceEndpoints
{
    public static WebApplication MapDeviceEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/fleet/summary", async (DeviceQueries queries, CancellationToken cancellationToken) =>
                TypedResults.Ok(await queries.GetSummaryAsync(cancellationToken)))
            .WithName("GetFleetSummary")
            .WithTags("Fleet")
            .WithSummary("Get fleet and regional connection and health counts");

        api.MapGet("/devices", SearchAsync)
            .WithName("SearchDevices")
            .WithTags("Devices")
            .WithSummary("Search and filter devices")
            .Produces<DevicePage>()
            .ProducesValidationProblem();

        api.MapGet("/devices/{id:guid}", GetDetailAsync)
            .WithName("GetDevice")
            .WithTags("Devices")
            .WithSummary("Get a device and its latest telemetry")
            .Produces<DeviceDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> SearchAsync(string? q, int? regionId, int? siteId,
        string? status, string? health, int? page, int? pageSize,
        DeviceQueries queries, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (q?.Length > 100)
        {
            errors["q"] = ["Search text must be at most 100 characters."];
        }

        if (regionId is <= 0)
        {
            errors["regionId"] = ["Region ID must be positive."];
        }

        if (siteId is <= 0)
        {
            errors["siteId"] = ["Site ID must be positive."];
        }

        var pageNumber = page ?? 1;
        if (pageNumber < 1)
        {
            errors["page"] = ["Page must be at least 1."];
        }

        var pageLength = pageSize ?? 25;
        if (pageLength is < 1 or > 100)
        {
            errors["pageSize"] = ["Page size must be between 1 and 100."];
        }

        bool? online = status?.ToLowerInvariant() switch
        {
            null => null,
            "online" => true,
            "offline" => false,
            _ => null
        };
        if (status is not null && online is null)
        {
            errors["status"] = ["Status must be online or offline."];
        }

        var healthFilter = health?.ToLowerInvariant();
        if (healthFilter is not null and not ("healthy" or "degraded" or "unhealthy"))
        {
            errors["health"] = ["Health must be healthy, degraded, or unhealthy."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var search = new DeviceSearch(q?.Trim(), regionId, siteId, online,
            healthFilter, pageNumber, pageLength);
        return TypedResults.Ok(await queries.SearchAsync(search, cancellationToken));
    }

    private static async Task<IResult> GetDetailAsync(Guid id, DeviceQueries queries,
        CancellationToken cancellationToken)
    {
        var device = await queries.GetDetailAsync(DeviceId.From(id), cancellationToken);
        return device is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Device not found")
            : TypedResults.Ok(device);
    }
}
