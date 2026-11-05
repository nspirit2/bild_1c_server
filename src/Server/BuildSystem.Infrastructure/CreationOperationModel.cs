using BuildSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Durable one-attempt ledger; FK pins an immutable restore intent, never an observation assignment.</summary>
public sealed class CreationOperationRecord
{
    public Guid OperationId { get; set; }
    public Guid RestoreIntentId { get; set; }
    public TargetCreationOperationState State { get; set; }
    public long Revision { get; set; }
    public Guid? AttemptId { get; set; }
    public long? AttemptExpectedRevision { get; set; }
    public TargetCreationQuarantineReason? QuarantineReason { get; set; }
    public bool Executable { get; set; }
    public bool ScheduledJobsMustRemainBlocked { get; set; } = true;
    public override string ToString() => "CreationOperationRecord [NONEXECUTABLE]";
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<CreationOperationRecord> TargetCreationOperations => Set<CreationOperationRecord>();

    private static void ConfigureCreationOperationModel(ModelBuilder b)
    {
        var row = b.Entity<CreationOperationRecord>();
        row.HasKey(x => x.OperationId);
        row.Property(x => x.OperationId).ValueGeneratedNever();
        row.HasIndex(x => x.RestoreIntentId).IsUnique();
        row.HasIndex(x => x.AttemptId).IsUnique().HasFilter("[AttemptId] IS NOT NULL");
        row.HasOne<RestoreIntentRecord>().WithMany().HasForeignKey(x => x.RestoreIntentId).OnDelete(DeleteBehavior.Restrict);
        row.Property(x => x.Revision).IsConcurrencyToken();
        row.Property(x => x.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        row.Property(x => x.QuarantineReason).HasConversion<string>().HasMaxLength(32);
        row.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TargetCreationOperations_Ids", "[OperationId] <> '00000000-0000-0000-0000-000000000000' AND [RestoreIntentId] <> '00000000-0000-0000-0000-000000000000' AND ([AttemptId] IS NULL OR [AttemptId] <> '00000000-0000-0000-0000-000000000000')");
            t.HasCheckConstraint("CK_TargetCreationOperations_NonExecutable", "[Executable] = 0");
            t.HasCheckConstraint("CK_TargetCreationOperations_JobsBlocked", "[ScheduledJobsMustRemainBlocked] = 1");
            t.HasCheckConstraint("CK_TargetCreationOperations_RevisionState", "([State] = 'Registered' AND [Revision] = 1 AND [AttemptId] IS NULL AND [AttemptExpectedRevision] IS NULL AND [QuarantineReason] IS NULL) OR ([State] = 'AttemptRecorded' AND [Revision] = 2 AND [AttemptId] IS NOT NULL AND [AttemptExpectedRevision] IS NOT NULL AND [AttemptExpectedRevision] = 1 AND [QuarantineReason] IS NULL) OR ([State] = 'ReconciliationRequired' AND [Revision] IN (2, 3) AND [QuarantineReason] IS NOT NULL AND [QuarantineReason] IN ('OutcomeUnknown', 'Cancelled', 'AuthorityRejected', 'TargetConflict') AND (([Revision] = 2 AND [AttemptId] IS NULL AND [AttemptExpectedRevision] IS NULL) OR ([Revision] = 3 AND [AttemptId] IS NOT NULL AND [AttemptExpectedRevision] IS NOT NULL AND [AttemptExpectedRevision] = 1)))");
        });
    }
}
// end
