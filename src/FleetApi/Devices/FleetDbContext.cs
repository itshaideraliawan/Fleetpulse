using FleetPulse.Contracts.Ids;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Devices;

public sealed class FleetDbContext(DbContextOptions<FleetDbContext> options) : DbContext(options)
{
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Device> Devices => Set<Device>();

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
    }
}
