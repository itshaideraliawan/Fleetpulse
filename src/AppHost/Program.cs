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

builder.AddProject<Projects.Gateway>("gateway")
    .WithReference(redis)
    .WithReference(rabbitMq)
    .WaitFor(redis)
    .WaitFor(rabbitMq);

builder.Build().Run();
