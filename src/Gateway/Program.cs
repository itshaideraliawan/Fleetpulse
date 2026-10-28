using FleetPulse.Contracts.Time;
using FleetPulse.Gateway.Devices;
using FleetPulse.ServiceDefaults;
using Microsoft.AspNetCore.Http.Connections;
using StackExchange.Redis;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddDeviceAdmissionControl(builder.Configuration);
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(DeviceMessageMetrics.MeterName));
builder.UseWolverine(GatewayMessaging.Configure);
builder.Services.AddSingleton<DeviceMessageBuffer>();
builder.Services.AddSingleton<IDeviceBatchSink, WolverineBatchSink>();
builder.Services.AddSingleton<ICommandReportSink, WolverineCommandReportSink>();
builder.Services.AddHostedService<DeviceMessagePublisher>();
var redisConnectionString = builder.Configuration.GetConnectionString("redis")
    ?? throw new InvalidOperationException("The Redis connection string is required.");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString));
builder.Services.AddSingleton<IDevicePresence, RedisDevicePresence>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<PendingDeviceCommands>();
builder.Services.AddSingleton<IDeviceCommandDelivery, GatewayCommandDelivery>();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = DeviceMessageValidator.MaxSignalRMessageBytes)
    .AddStackExchangeRedis(redisConnectionString, options =>
    options.Configuration.ChannelPrefix = RedisChannel.Literal("fleetpulse:gateway"));

var app = builder.Build();
app.UseDeviceAdmissionControl();
app.MapDefaultEndpoints();
app.MapHub<DeviceHub>(DeviceHub.Route, options => options.Transports = HttpTransportType.WebSockets);
app.Run();
