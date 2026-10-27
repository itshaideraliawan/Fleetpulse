using FleetPulse.Contracts.Ids;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Rollouts;

internal static class RolloutModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Release>(entity =>
        {
            entity.HasKey(release => release.Id);
            entity.Property(release => release.Id)
                .HasConversion(id => id.Value, value => ReleaseId.From(value)).ValueGeneratedNever();
            entity.Property(release => release.FirmwareVersion).HasMaxLength(40).IsRequired();
            entity.HasIndex(release => release.FirmwareVersion).IsUnique();
        });

        modelBuilder.Entity<Rollout>(entity =>
        {
            entity.HasKey(rollout => rollout.Id);
            entity.Property(rollout => rollout.Id)
                .HasConversion(id => id.Value, value => RolloutId.From(value)).ValueGeneratedNever();
            entity.Property(rollout => rollout.ReleaseId)
                .HasConversion(id => id.Value, value => ReleaseId.From(value));
            entity.Property(rollout => rollout.Status).HasConversion<int>();
            entity.Property(rollout => rollout.StateReason).HasMaxLength(512).IsRequired();
            entity.Property(rollout => rollout.TransitionVersion).IsConcurrencyToken();
            entity.HasMany(rollout => rollout.Transitions).WithOne()
                .HasForeignKey(transition => transition.RolloutId);
            entity.Navigation(rollout => rollout.Transitions).HasField("_transitions")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.HasOne(rollout => rollout.Release).WithMany(release => release.Rollouts)
                .HasForeignKey(rollout => rollout.ReleaseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(rollout => rollout.CreatedAtUtc);
        });

        modelBuilder.Entity<RolloutTransition>(entity =>
        {
            entity.HasKey(transition => transition.Id);
            entity.Property(transition => transition.Id).ValueGeneratedNever();
            entity.Property(transition => transition.RolloutId)
                .HasConversion(id => id.Value, value => RolloutId.From(value));
            entity.Property(transition => transition.FromStatus).HasConversion<int>();
            entity.Property(transition => transition.ToStatus).HasConversion<int>();
            entity.Property(transition => transition.Reason).HasMaxLength(RolloutStateMachine.MaxReasonLength).IsRequired();
            entity.HasIndex(transition => new { transition.RolloutId, transition.Sequence }).IsUnique();
        });

        modelBuilder.Entity<Wave>(entity =>
        {
            entity.HasKey(wave => wave.Id);
            entity.Property(wave => wave.Id)
                .HasConversion(id => id.Value, value => WaveId.From(value)).ValueGeneratedNever();
            entity.Property(wave => wave.RolloutId)
                .HasConversion(id => id.Value, value => RolloutId.From(value));
            entity.HasAlternateKey(wave => new { wave.RolloutId, wave.Id });
            entity.HasIndex(wave => new { wave.RolloutId, wave.Number }).IsUnique();
            entity.HasOne(wave => wave.Rollout).WithMany(rollout => rollout.Waves)
                .HasForeignKey(wave => wave.RolloutId);
        });

        modelBuilder.Entity<WaveTarget>(entity =>
        {
            entity.HasKey(target => new { target.RolloutId, target.DeviceId });
            entity.Property(target => target.RolloutId)
                .HasConversion(id => id.Value, value => RolloutId.From(value));
            entity.Property(target => target.WaveId)
                .HasConversion(id => id.Value, value => WaveId.From(value));
            entity.Property(target => target.DeviceId)
                .HasConversion(id => id.Value, value => DeviceId.From(value));
            entity.Property(target => target.OriginalFirmwareVersion).HasMaxLength(40).IsRequired();
            entity.HasOne(target => target.Wave).WithMany(wave => wave.Targets)
                .HasForeignKey(target => new { target.RolloutId, target.WaveId })
                .HasPrincipalKey(wave => new { wave.RolloutId, wave.Id });
            entity.HasOne<Device>().WithMany().HasForeignKey(target => target.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
