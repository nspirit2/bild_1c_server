using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Immutable worker attestation of pinned bytes, not format validity or environment readiness.</summary>
public sealed class StagedArtifact
{
    public Guid ArtifactId { get; set; }
    public Guid JobId { get; set; }
    public Guid AgentId { get; set; }
    public long FencingToken { get; set; }
    public Guid ReservationId { get; set; }
    public Guid ArchiveVersionId { get; set; }
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Format { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string State { get; set; } = "ContentVerified";
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<StagedArtifact> StagedArtifacts => Set<StagedArtifact>();

    private static void ConfigureArtifactModel(ModelBuilder b)
    {
        var artifact = b.Entity<StagedArtifact>();
        artifact.HasKey(x => x.ArtifactId);
        artifact.Property(x => x.ArtifactId).ValueGeneratedNever();
        artifact.HasIndex(x => x.JobId).IsUnique();
        artifact.HasIndex(x => new { x.ReservationId, x.ArchiveVersionId }).IsUnique();
        artifact.Property(x => x.Sha256).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2");
        artifact.Property(x => x.Format).HasMaxLength(16);
        artifact.Property(x => x.State).HasMaxLength(32);
        artifact.HasOne<EnvironmentJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        artifact.HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        artifact.HasOne<EnvironmentReservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        artifact.HasOne<DiskArchiveVersion>().WithMany().HasForeignKey(x => x.ArchiveVersionId).OnDelete(DeleteBehavior.Restrict);
    }
}
