using Microsoft.EntityFrameworkCore;
namespace BuildSystem.Infrastructure;

/// <summary>Immutable full staging allocation release ledger; historical reservation fields stay unchanged.</summary>
public sealed class StagingAllocationReleaseRecord
{
    public Guid ReleaseId { get; set; }
    public Guid ResultId { get; set; }
    public Guid AuthorizationId { get; set; }
    public Guid ReservationId { get; set; }
    public Guid RootId { get; set; }
    public Guid PoolId { get; set; }
    public long ReleasedBytes { get; set; }
    public DateTimeOffset ReleasedAt { get; set; }
    public Guid ActorId { get; set; }
}
public sealed partial class BuildSystemDbContext
{
    public DbSet<StagingAllocationReleaseRecord> StagingAllocationReleases => Set<StagingAllocationReleaseRecord>();
    private static void ConfigureStagingAllocationReleaseModel(ModelBuilder b)
    {
        var row = b.Entity<StagingAllocationReleaseRecord>();
        row.HasKey(x => x.ReleaseId); row.Property(x => x.ReleaseId).ValueGeneratedNever();
        row.HasIndex(x => x.ResultId).IsUnique(); row.HasIndex(x => x.ReservationId).IsUnique();
        row.HasOne<StageCleanupResultRecord>().WithMany().HasForeignKey(x => x.ResultId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<StageCleanupAuthorizationRecord>().WithMany().HasForeignKey(x => x.AuthorizationId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<EnvironmentReservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<ArchiveRoot>().WithMany().HasForeignKey(x => x.RootId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<EnvironmentPool>().WithMany().HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<BuildSystemUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
    }
}
// end
