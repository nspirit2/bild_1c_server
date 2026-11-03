using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Immutable NONEXECUTABLE intent and permanent retained-artifact hold. No release API.</summary>
public sealed class RestoreIntentRecord
{
    public Guid RestoreIntentId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid StageJobId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid RequestId { get; set; }
    public Guid PoolId { get; set; }
    public Guid RootId { get; set; }
    public Guid ArchiveVersionId { get; set; }
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Format { get; set; } = "";
    public string SnapshotSha256 { get; set; } = "";
    public string PreparationProfileVersion { get; set; } = "";
    public bool ScheduledJobsMustRemainBlocked { get; set; } = true;
    public Guid AgentId { get; set; }
    public long FencingToken { get; set; }
    public Guid TargetIncarnation { get; set; }
    public Guid CanonicalClusterId { get; set; }
    public Guid CanonicalSqlServerId { get; set; }
    public string InfobaseName { get; set; } = "";
    public string SqlDbName { get; set; } = "";
    public Guid RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public bool Executable { get; set; }
    public override string ToString() => "RestoreIntentRecord [NONEXECUTABLE]";
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<RestoreIntentRecord> RestoreIntents => Set<RestoreIntentRecord>();
    private static void ConfigureRestoreIntentModel(ModelBuilder b)
    {
        var row = b.Entity<RestoreIntentRecord>();
        row.HasKey(x => x.RestoreIntentId);
        row.Property(x => x.RestoreIntentId).ValueGeneratedNever();
        row.Property(x => x.InfobaseName).HasMaxLength(40).UseCollation("Latin1_General_100_BIN2");
        row.Property(x => x.SqlDbName).HasMaxLength(40).UseCollation("Latin1_General_100_BIN2");
        row.Property(x => x.Sha256).HasMaxLength(64);
        row.Property(x => x.SnapshotSha256).HasMaxLength(64);
        row.Property(x => x.Format).HasMaxLength(16);
        row.Property(x => x.PreparationProfileVersion).HasMaxLength(128);
        row.HasIndex(x => x.EnvironmentId).IsUnique();
        row.HasIndex(x => x.StageJobId).IsUnique();
        row.HasIndex(x => x.ReservationId).IsUnique();
        row.HasIndex(x => x.ArtifactId).IsUnique();
        row.HasIndex(x => x.TargetIncarnation).IsUnique();
        row.HasIndex(x => new { x.CanonicalSqlServerId, x.SqlDbName }).IsUnique();
        row.HasIndex(x => new { x.CanonicalClusterId, x.InfobaseName }).IsUnique();
        row.ToTable(t =>
        {
            t.HasCheckConstraint("CK_RestoreIntents_NonExecutable", "[Executable] = 0");
            t.HasCheckConstraint("CK_RestoreIntents_ScheduledJobsBlocked", "[ScheduledJobsMustRemainBlocked] = 1");
        });
        row.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<EnvironmentJob>().WithMany().HasForeignKey(x => x.StageJobId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<EnvironmentReservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<StagedArtifact>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<BuildSystemUser>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
// end
