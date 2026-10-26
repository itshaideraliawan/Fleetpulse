using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using FleetPulse.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddDbContext<FleetDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("fleetdb")));
builder.Services.AddSingleton<IClock, SystemClock>();
var redisConnectionString = builder.Configuration.GetConnectionString("redis")
    ?? throw new InvalidOperationException("The Redis connection string is required.");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));
builder.Services.AddSingleton<RedisFleetState>();
builder.Services.AddScoped<DeviceQueries>();
builder.Services.AddProblemDetails();
builder.Services.AddFleetOpenApi();
builder.Services.AddSignalR()
    .AddStackExchangeRedis(redisConnectionString, options =>
        options.Configuration.ChannelPrefix = RedisChannel.Literal("fleetpulse:operator"));
builder.Services.AddHostedService<OfflineDetectionWorker>();
builder.Services.AddHostedService<StatusBroadcaster>();
builder.UseWolverine(FleetMessaging.Configure);

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
    await db.Database.MigrateAsync(app.Lifetime.ApplicationStopping);
    await FleetSeed.SeedAsync(db, app.Lifetime.ApplicationStopping);
}
app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapDeviceEndpoints();
app.MapHub<OperatorHub>(OperatorHub.Route);
app.Run();
