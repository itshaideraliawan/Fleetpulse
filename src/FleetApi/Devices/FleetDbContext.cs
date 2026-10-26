using FleetPulse.Contracts.Ids;
using FleetPulse.FleetApi.Commands;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Devices;

public sealed class FleetDbContext(DbContextOptions<FleetDbContext> options) : DbContext(options)
{
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<CommandTarget> CommandTargets => Set<CommandTarget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Region>(entity =>
        {
            entity.HasKey(region => region.Id);
            entity.Property(region => region.Id).ValueGeneratedNever();
            entity.Property(region => region.Name).HasMaxLength(80).IsRequired();
        });

        modelBuilder.Entity<Site>(entity =>
        {
            entity.HasKey(site => site.Id);
            entity.Property(site => site.Id).ValueGeneratedNever();
            entity.Property(site => site.Name).HasMaxLength(100).IsRequired();
            entity.HasOne(site => site.Region).WithMany(region => region.Sites).HasForeignKey(site => site.RegionId);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.HasKey(device => device.Id);
            entity.Property(device => device.Id).HasConversion(id => id.Value, value => DeviceId.From(value)).ValueGeneratedNever();
            entity.Property(device => device.SerialNumber).HasMaxLength(32).IsRequired();
            entity.HasIndex(device => device.SerialNumber).IsUnique();
            entity.Property(device => device.Model).HasMaxLength(40).IsRequired();
            entity.Property(device => device.FirmwareVersion).HasMaxLength(40).IsRequired();
            entity.HasOne(device => device.Site).WithMany(site => site.Devices).HasForeignKey(device => device.SiteId);
            entity.HasIndex(device => device.SiteId);
        });

        modelBuilder.Entity<Command>(entity =>
        {
            entity.HasKey(command => command.Id);
            entity.Property(command => command.Id).ValueGeneratedNever();
            entity.Property(command => command.Kind).HasConversion<int>();
            entity.HasIndex(command => command.CreatedAtUtc);
        });

        modelBuilder.Entity<CommandTarget>(entity =>
        {
            entity.HasKey(target => target.Id);
            entity.Property(target => target.Id)
                .HasConversion(id => id.Value, value => CommandId.From(value))
                .ValueGeneratedNever();
            entity.Property(target => target.DeviceId)
                .HasConversion(id => id.Value, value => DeviceId.From(value));
            entity.Property(target => target.Status).HasConversion<int>();
            entity.HasOne(target => target.Command).WithMany(command => command.Targets)
                .HasForeignKey(target => target.CommandGroupId);
            entity.HasOne<Device>().WithMany().HasForeignKey(target => target.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(target => new { target.CommandGroupId, target.Status });
            entity.HasIndex(target => target.DeviceId);
        });
    }
}
