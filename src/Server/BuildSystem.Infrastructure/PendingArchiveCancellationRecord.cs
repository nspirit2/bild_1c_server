using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Durable receipt for cancellation of an archive order before its first lease.</summary>
public sealed class PendingArchiveCancellationRecord
{
    public Guid EnvironmentId { get; init; }
    public Guid RequestId { get; init; }
    public Guid JobId { get; init; }
    public Guid ReservationId { get; init; }
    public Guid RootId { get; init; }
    public Guid PoolId { get; init; }
    public Guid ActorId { get; init; }
    public DateTimeOffset CancelledAt { get; init; }
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<PendingArchiveCancellationRecord> PendingArchiveCancellations => Set<PendingArchiveCancellationRecord>();

    private void GuardPendingArchiveCancellations()
    {
        if (ChangeTracker.Entries<PendingArchiveCancellationRecord>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Pending archive cancellation receipts are immutable.");
    }

    private static void ConfigurePendingArchiveCancellationModel(ModelBuilder b)
    {
        var row = b.Entity<PendingArchiveCancellationRecord>();
        row.HasKey(x => x.EnvironmentId);
        row.Property(x => x.EnvironmentId).ValueGeneratedNever();
        row.HasIndex(x => x.RequestId).IsUnique();
        row.HasIndex(x => x.JobId).IsUnique();
        row.HasIndex(x => x.ReservationId).IsUnique();
        row.HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<EnvironmentRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<EnvironmentJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<EnvironmentReservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<ArchiveRoot>().WithMany().HasForeignKey(x => x.RootId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<EnvironmentPool>().WithMany().HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Restrict);
        row.Property(x => x.CancelledAt).HasPrecision(7);
        row.ToTable("PendingArchiveCancellations", t =>
        {
            t.HasTrigger("TR_PendingArchiveCancellations_AppendOnly");
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_PendingArchiveCancellations_CancelledAt", "DATEPART(TZOFFSET, [CancelledAt]) = 0 AND [CancelledAt] > '0001-01-01T00:00:00+00:00'");
            t.HasCheckConstraint("CK_PendingArchiveCancellations_Ids", """
                [EnvironmentId] <> '00000000-0000-0000-0000-000000000000'
                AND [RequestId] <> '00000000-0000-0000-0000-000000000000'
                AND [JobId] <> '00000000-0000-0000-0000-000000000000'
                AND [ReservationId] <> '00000000-0000-0000-0000-000000000000'
                AND [RootId] <> '00000000-0000-0000-0000-000000000000'
                AND [PoolId] <> '00000000-0000-0000-0000-000000000000'
                AND [ActorId] <> '00000000-0000-0000-0000-000000000000'
                """);
        });
    }
}
