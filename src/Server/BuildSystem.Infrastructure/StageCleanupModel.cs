using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Immutable, single-artifact authorization; no completion, resource release, expiry or reassignment.</summary>
public sealed class StageCleanupAuthorizationRecord
{
    public Guid AuthorizationId { get; set; }
    public Guid RetirementId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid AgentId { get; set; }
    public long StageFencingToken { get; set; }
    public DateTimeOffset AuthorizedAt { get; set; }
    public string PlanJson { get; set; } = "";
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<StageCleanupAuthorizationRecord> StageCleanupAuthorizations => Set<StageCleanupAuthorizationRecord>();
    private static void ConfigureStageCleanupModel(ModelBuilder b)
    {
        var row = b.Entity<StageCleanupAuthorizationRecord>();
        row.HasKey(x => x.AuthorizationId);
        row.Property(x => x.AuthorizationId).ValueGeneratedNever();
        row.Property(x => x.PlanJson).HasMaxLength(2048);
        row.HasIndex(x => x.RetirementId).IsUnique();
        row.HasIndex(x => x.ArtifactId).IsUnique();
        row.HasOne<StageRetirementIntent>().WithMany().HasForeignKey(x => x.RetirementId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<StagedArtifact>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
    }
}
// end
