var builder = DistributedApplication.CreateBuilder(args);

var fleetDb = builder.AddSqlServer("sqlserver")
    .WithDataVolume()
    .AddDatabase("fleetdb");

var redis = builder.AddRedis("redis");
var rabbitMq = builder.AddRabbitMQ("rabbitmq");

builder.AddProject<Projects.FleetApi>("fleet-api")
    .WithReference(fleetDb)
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WaitFor(fleetDb)
    .WaitFor(redis)
    .WaitFor(rabbitMq);

var gateway = builder.AddProject<Projects.Gateway>("gateway")
    .WithHttpEndpoint()
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WaitFor(redis)
    .WaitFor(rabbitMq);

var simulatorDevicesValue = builder.Configuration["Simulator:Devices"];
var simulatorDevices = simulatorDevicesValue is null ? 1000
    : int.TryParse(simulatorDevicesValue, out var configuredDevices) ? configuredDevices
    : throw new ArgumentException("Simulator:Devices must be a number from 0 to 5000.");

if (simulatorDevices is < 0 or > 5000)
{
    throw new ArgumentException("Simulator:Devices must be 0 to disable the Simulator or 1 to 5000 devices.");
}

if (simulatorDevices > 0)
{
    var commandFailureRate = builder.Configuration["Simulator:CommandFailureRate"] ?? "0";
    builder.AddProject<Projects.Simulator>("simulator")
        .WithEnvironment("GATEWAY_URL", gateway.GetEndpoint("http"))
        .WithArgs("--devices", simulatorDevices.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--command-failure-rate", commandFailureRate)
        .WaitFor(gateway);
}

builder.Build().Run();
