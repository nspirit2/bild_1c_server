using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Immutable machine report, not independently proven file removal or quota release.</summary>
public sealed class StageCleanupResultRecord
{
    public Guid ResultId { get; set; }
    public Guid AuthorizationId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid AgentId { get; set; }
    public long StageFencingToken { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<StageCleanupResultRecord> StageCleanupResults => Set<StageCleanupResultRecord>();
    private static void ConfigureStageCleanupResultModel(ModelBuilder b)
    {
        var row = b.Entity<StageCleanupResultRecord>();
        row.HasKey(x => x.ResultId);
        row.Property(x => x.ResultId).ValueGeneratedNever();
        row.HasIndex(x => x.AuthorizationId).IsUnique();
        row.HasIndex(x => x.ArtifactId).IsUnique();
        row.HasOne<StageCleanupAuthorizationRecord>().WithMany().HasForeignKey(x => x.AuthorizationId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<StagedArtifact>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
    }
}
// end
