using FleetPulse.FleetApi.Commands;
using FleetPulse.FleetApi.Rollouts;
using FleetPulse.Contracts.Time;
using FleetPulse.FleetApi.Devices;
using FleetPulse.ServiceDefaults;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Wolverine;
using FleetPulse.FleetApi.Demo;
using FleetPulse.Contracts.Messaging;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var sqlConnectionString = builder.Configuration.GetConnectionString("fleetdb")
    ?? throw new InvalidOperationException("The fleet database connection string is required.");
builder.Services.AddDbContext<FleetDbContext>(options =>
    options.UseSqlServer(sqlConnectionString));
builder.Services.AddSingleton<IClock, SystemClock>();
var redisConnectionString = builder.Configuration.GetConnectionString("redis")
    ?? throw new InvalidOperationException("The Redis connection string is required.");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));
builder.Services.AddSingleton<RedisFleetState>();
builder.Services.AddScoped<DeviceQueries>();
builder.Services.AddScoped<CommandPublisher>();
builder.Services.AddScoped<CommandQueries>();
builder.Services.AddScoped<RolloutPlanner>();
builder.Services.AddScoped<RolloutQueries>();
builder.Services.AddScoped<DemoQueries>();
builder.Services.AddSingleton<RolloutStateMachine>();
builder.Services.AddScoped<RolloutOrchestrator>();
builder.Services.AddProblemDetails();
builder.Services.AddFleetOpenApi();
builder.Services.AddSignalR()
    .AddStackExchangeRedis(redisConnectionString, options =>
        options.Configuration.ChannelPrefix = RedisChannel.Literal("fleetpulse:operator"));
builder.Services.AddHostedService<OfflineDetectionWorker>();
builder.Services.AddHostedService<CommandRetryWorker>();
builder.Services.AddHostedService<RolloutWorker>();
builder.Services.AddHostedService<DemoResetCompletionWorker>();
builder.Services.AddHostedService<StatusBroadcaster>();
builder.UseWolverine(options =>
{
    FleetMessaging.Configure(options);
    FleetMessaging.ConfigureOutbox(options, sqlConnectionString);
});

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
    await db.Database.MigrateAsync(app.Lifetime.ApplicationStopping);
    await FleetSeed.SeedAsync(db, app.Lifetime.ApplicationStopping);
    var demo = await db.DemoControls.AsNoTracking().SingleAsync(app.Lifetime.ApplicationStopping);
    await DemoControlHandler.HandleAsync(new DemoControlChanged(demo.Snapshot()), db,
        scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>(), app.Lifetime.ApplicationStopping);
}
app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapDeviceEndpoints();
app.MapCommandEndpoints();
app.MapRolloutEndpoints();
app.MapDemoEndpoints();
app.MapHub<OperatorHub>(OperatorHub.Route);
app.Run();
