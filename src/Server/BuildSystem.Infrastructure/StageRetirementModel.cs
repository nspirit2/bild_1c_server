using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Immutable human intent only. No cleanup dispatch, resource release, or retirement receipt.</summary>
public sealed class StageRetirementIntent
{
    public Guid RetirementId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid JobId { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid AgentId { get; set; }
    public long FencingToken { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public string State { get; set; } = "Requested";
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<StageRetirementIntent> StageRetirementIntents => Set<StageRetirementIntent>();
    private static void ConfigureStageRetirementModel(ModelBuilder b)
    {
        var row = b.Entity<StageRetirementIntent>();
        row.HasKey(x => x.RetirementId);
        row.Property(x => x.RetirementId).ValueGeneratedNever();
        row.Property(x => x.State).HasMaxLength(32);
        row.HasIndex(x => x.EnvironmentId).IsUnique();
        row.HasIndex(x => x.JobId).IsUnique();
        row.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<EnvironmentJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<StagedArtifact>().WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<EnvironmentReservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<BuildSystemUser>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
// end
