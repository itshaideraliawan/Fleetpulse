using FleetPulse.Contracts.Messaging;
using FleetPulse.FleetApi.Devices;
using Microsoft.EntityFrameworkCore;

namespace FleetPulse.FleetApi.Demo;

public sealed class DemoControlState
{
    public int Id { get; set; } = 1;
    public long Revision { get; set; }
    public long WorkVersion { get; set; }
    public Guid LastRequestId { get; set; }
    public bool AdmissionEnabled { get; set; } = true;
    public string? BadReleaseVersion { get; set; }
    public string? BadReleaseModel { get; set; }
    public long DropGeneration { get; set; }
    public long ResetGeneration { get; set; }
    public DateTimeOffset? ResetAtUtc { get; set; }
    public DateTimeOffset? DropAtUtc { get; set; }
    public int RecoveryTarget { get; set; }
    public bool ResetPending { get; set; }

    public DemoControlSnapshot Snapshot() => new(Revision, AdmissionEnabled,
        BadReleaseVersion, BadReleaseModel, DropGeneration, ResetGeneration, ResetAtUtc);

    public static async Task TouchWorkAsync(FleetDbContext db, CancellationToken cancellationToken)
    {
        var state = await db.DemoControls.SingleAsync(cancellationToken);
        if (state.ResetPending) throw new DbUpdateConcurrencyException("Fleet reset is still being applied.");
        state.WorkVersion++;
    }
}
