using FleetPulse.Contracts.Ids;
using Microsoft.OpenApi;

namespace FleetPulse.FleetApi.Devices;

public static class DeviceOpenApi
{
    public static IServiceCollection AddFleetOpenApi(this IServiceCollection services) =>
        services.AddOpenApi(options => options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Type == typeof(DeviceId) || context.JsonTypeInfo.Type == typeof(CommandId) ||
                context.JsonTypeInfo.Type == typeof(RolloutId))
            {
                schema.Type = JsonSchemaType.String;
                schema.Format = "uuid";
            }

            return Task.CompletedTask;
        }));
}
