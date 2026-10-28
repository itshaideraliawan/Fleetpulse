using System.Globalization;
using Microsoft.AspNetCore.SignalR;

namespace FleetPulse.Gateway.Devices;

public sealed class DeviceAdmissionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ConnectionAdmissionControl admission)
    {
        if (!context.WebSockets.IsWebSocketRequest ||
            context.GetEndpoint()?.Metadata.GetMetadata<HubMetadata>()?.HubType != typeof(DeviceHub))
        {
            await next(context);
            return;
        }

        var decision = admission.TryAcquire();
        DeviceMessageMetrics.RecordAdmission(decision.Allowed);
        if (decision.Allowed)
        {
            await next(context);
            return;
        }

        context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
            title: "Device connection admission limit reached",
            detail: "Retry after the indicated delay with jitter.",
            extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = decision.RetryAfterSeconds })
            .ExecuteAsync(context);
    }
}

public static class DeviceAdmissionExtensions
{
    public static IServiceCollection AddDeviceAdmissionControl(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AdmissionControlOptions>().Bind(configuration.GetSection(AdmissionControlOptions.SectionName))
            .Validate(options => options.PermitsPerSecond is >= 1 and <= 100_000,
                "AdmissionControl:PermitsPerSecond must be between 1 and 100000.")
            .Validate(options => options.BurstCapacity is >= 1 and <= 100_000,
                "AdmissionControl:BurstCapacity must be between 1 and 100000.")
            .ValidateOnStart();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ConnectionAdmissionControl>();
        return services;
    }

    public static WebApplication UseDeviceAdmissionControl(this WebApplication app)
    {
        app.UseRouting();
        app.UseWebSockets();
        app.UseMiddleware<DeviceAdmissionMiddleware>();
        return app;
    }
}
