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

builder.AddProject<Projects.Simulator>("simulator")
    .WithEnvironment("GATEWAY_URL", gateway.GetEndpoint("http"))
    .WaitFor(gateway);

builder.Build().Run();
