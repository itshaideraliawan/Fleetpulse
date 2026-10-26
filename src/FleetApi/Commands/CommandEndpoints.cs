using FleetPulse.Contracts.DeviceProtocol;

namespace FleetPulse.FleetApi.Commands;

public sealed record CreateCommandRequest(CommandKind Kind, Guid? DeviceId,
    int? RegionId, string? Model, bool Fleet, int? ExpiresInSeconds);

public sealed record CreateCommandResult(Guid CommandId, int TargetCount);

public static class CommandEndpoints
{
    public static WebApplication MapCommandEndpoints(this WebApplication app)
    {
        app.MapPost("/api/commands", CreateAsync)
            .WithName("CreateCommand")
            .WithTags("Commands")
            .WithSummary("Create a diagnostics command for a device, group, or fleet")
            .Produces<CreateCommandResult>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<IResult> CreateAsync(CreateCommandRequest request,
        CommandPublisher publisher, CancellationToken cancellationToken)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var normalized = request with { Model = request.Model?.Trim() };
        var result = await publisher.CreateAsync(normalized, cancellationToken);
        return result is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound,
                title: "No devices match the command target")
            : TypedResults.Accepted((string?)null, result);
    }

    private static Dictionary<string, string[]> Validate(CreateCommandRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.Kind != CommandKind.RunDiagnostics)
        {
            errors["kind"] = ["Only diagnostics commands are currently supported."];
        }

        if (request.DeviceId == Guid.Empty)
        {
            errors["deviceId"] = ["Device ID must not be empty."];
        }

        if (request.RegionId is <= 0)
        {
            errors["regionId"] = ["Region ID must be positive."];
        }

        if (request.Model is not null &&
            (string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 40))
        {
            errors["model"] = ["Model must be between 1 and 40 characters."];
        }

        if (request.ExpiresInSeconds is < 10 or > 86400)
        {
            errors["expiresInSeconds"] = ["Expiry must be between 10 and 86400 seconds."];
        }

        var device = request.DeviceId is not null;
        var group = request.RegionId is not null || request.Model is not null;
        if ((device ? 1 : 0) + (group ? 1 : 0) + (request.Fleet ? 1 : 0) != 1)
        {
            errors["target"] = ["Select one target: device, region/model group, or fleet."];
        }

        return errors;
    }
}
