using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>One immutable historical observation report per assignment; never grants execution authority.</summary>
public sealed class TargetObservationReceiptRecord
{
    public Guid ReceiptId { get; set; }
    public Guid ObservationRequestId { get; set; }
    public Guid RacObservationId { get; set; }
    public Guid SqlObservationId { get; set; }
    public int Outcome { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public bool Executable { get; set; }
    public bool ScheduledJobsMustRemainBlocked { get; set; } = true;
    public override string ToString() => "TargetObservationReceiptRecord [NONEXECUTABLE; details omitted]";
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<TargetObservationReceiptRecord> TargetObservationReceipts => Set<TargetObservationReceiptRecord>();

    private static void ConfigureTargetObservationReceiptModel(ModelBuilder b)
    {
        var row = b.Entity<TargetObservationReceiptRecord>();
        row.HasKey(x => x.ReceiptId);
        row.Property(x => x.ReceiptId).ValueGeneratedNever();
        row.HasIndex(x => x.ObservationRequestId).IsUnique();
        row.HasIndex(x => x.RacObservationId).IsUnique();
        row.HasIndex(x => x.SqlObservationId).IsUnique();
        row.HasOne<TargetObservationRecord>().WithMany().HasForeignKey(x => x.ObservationRequestId)
            .OnDelete(DeleteBehavior.Restrict);
        row.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TargetObservationReceipts_NonExecutable", "[Executable] = 0");
            t.HasCheckConstraint("CK_TargetObservationReceipts_ScheduledJobsBlocked", "[ScheduledJobsMustRemainBlocked] = 1");
            t.HasCheckConstraint("CK_TargetObservationReceipts_Outcome", "[Outcome] BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_TargetObservationReceipts_DistinctObservationIds", "[RacObservationId] <> [SqlObservationId]");
            t.HasCheckConstraint("CK_TargetObservationReceipts_NonemptyIds", "[ReceiptId] <> '00000000-0000-0000-0000-000000000000' AND [ObservationRequestId] <> '00000000-0000-0000-0000-000000000000' AND [RacObservationId] <> '00000000-0000-0000-0000-000000000000' AND [SqlObservationId] <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}
// end
