using FleetPulse.FleetApi.Devices;
using FleetPulse.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddDbContext<FleetDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("fleetdb")));

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
    await db.Database.MigrateAsync(app.Lifetime.ApplicationStopping);
    await FleetSeed.SeedAsync(db, app.Lifetime.ApplicationStopping);
}
app.MapDefaultEndpoints();
app.Run();
