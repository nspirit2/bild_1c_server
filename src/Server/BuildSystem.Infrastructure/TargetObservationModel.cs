using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Immutable, NONEXECUTABLE observation assignment; correlation is not a fresh execution fence.</summary>
public sealed class TargetObservationRecord
{
    public Guid ObservationRequestId { get; set; }
    public Guid RestoreIntentId { get; set; }
    public Guid AgentId { get; set; }
    public Guid OperationId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid StageJobId { get; set; }
    public long FencingToken { get; set; }
    public Guid ReservationId { get; set; }
    public Guid TargetIncarnationId { get; set; }
    public Guid CanonicalClusterId { get; set; }
    public Guid CanonicalSqlServerId { get; set; }
    public Guid RacEndpointMappingId { get; set; }
    public Guid SqlEndpointMappingId { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public bool Executable { get; set; }
    public bool ScheduledJobsMustRemainBlocked { get; set; } = true;
    public override string ToString() => "TargetObservationRecord [NONEXECUTABLE; details omitted]";
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<TargetObservationRecord> TargetObservations => Set<TargetObservationRecord>();

    private static void ConfigureTargetObservationModel(ModelBuilder b)
    {
        var row = b.Entity<TargetObservationRecord>();
        row.HasKey(x => x.ObservationRequestId);
        row.Property(x => x.ObservationRequestId).ValueGeneratedNever();
        row.HasIndex(x => x.RestoreIntentId).IsUnique();
        row.HasIndex(x => x.OperationId).IsUnique();
        row.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TargetObservations_NonExecutable", "[Executable] = 0");
            t.HasCheckConstraint("CK_TargetObservations_ScheduledJobsBlocked", "[ScheduledJobsMustRemainBlocked] = 1");
            t.HasCheckConstraint("CK_TargetObservations_PositiveFence", "[FencingToken] > 0");
            t.HasCheckConstraint("CK_TargetObservations_NonemptyIds", "[ObservationRequestId] <> '00000000-0000-0000-0000-000000000000' AND [RestoreIntentId] <> '00000000-0000-0000-0000-000000000000' AND [AgentId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000' AND [EnvironmentId] <> '00000000-0000-0000-0000-000000000000' AND [StageJobId] <> '00000000-0000-0000-0000-000000000000' AND [ReservationId] <> '00000000-0000-0000-0000-000000000000' AND [TargetIncarnationId] <> '00000000-0000-0000-0000-000000000000' AND [CanonicalClusterId] <> '00000000-0000-0000-0000-000000000000' AND [CanonicalSqlServerId] <> '00000000-0000-0000-0000-000000000000' AND [RacEndpointMappingId] <> '00000000-0000-0000-0000-000000000000' AND [SqlEndpointMappingId] <> '00000000-0000-0000-0000-000000000000' AND [RequestedBy] <> '00000000-0000-0000-0000-000000000000'");
        });
        row.HasOne<RestoreIntentRecord>().WithMany().HasForeignKey(x => x.RestoreIntentId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<BuildSystemUser>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
// end