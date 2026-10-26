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
builder.Services.AddHostedService<OfflineDetectionWorker>();
builder.UseWolverine(FleetMessaging.Configure);

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
    await db.Database.MigrateAsync(app.Lifetime.ApplicationStopping);
    await FleetSeed.SeedAsync(db, app.Lifetime.ApplicationStopping);
}
app.MapDefaultEndpoints();
app.Run();
