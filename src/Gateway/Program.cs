using FleetPulse.Gateway.Devices;
using FleetPulse.ServiceDefaults;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var redisConnectionString = builder.Configuration.GetConnectionString("redis")
    ?? throw new InvalidOperationException("The Redis connection string is required.");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));
builder.Services.AddSingleton<IDevicePresence, RedisDevicePresence>();
builder.Services.AddSignalR().AddStackExchangeRedis(redisConnectionString, options =>
    options.Configuration.ChannelPrefix = RedisChannel.Literal("fleetpulse:gateway"));

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapHub<DeviceHub>(DeviceHub.Route);
app.Run();
