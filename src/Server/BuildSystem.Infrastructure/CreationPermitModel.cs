using BuildSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Durable nonexecutable permit metadata; immutable operation/intent FKs retain all pins.</summary>
public sealed class CreationPermitRecord
{
    public Guid PermitId { get; set; }
    public Guid OperationId { get; set; }
    public Guid AttemptId { get; set; }
    public long ExpectedOperationRevision { get; set; } = 1;
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset StartBefore { get; set; }
    public TargetCreationPermitState State { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public long? StartExpectedRevision { get; set; }
    public bool Executable { get; set; }
    public bool ScheduledJobsMustRemainBlocked { get; set; } = true;
    public override string ToString() => "CreationPermitRecord [NONEXECUTABLE]";
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<CreationPermitRecord> TargetCreationPermits => Set<CreationPermitRecord>();

    private static void ConfigureCreationPermitModel(ModelBuilder b)
    {
        var row = b.Entity<CreationPermitRecord>();
        row.HasKey(x => x.PermitId);
        row.Property(x => x.PermitId).ValueGeneratedNever();
        row.HasIndex(x => x.OperationId).IsUnique();
        row.HasIndex(x => x.AttemptId).IsUnique();
        row.HasOne<CreationOperationRecord>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        row.Property(x => x.Revision).IsConcurrencyToken();
        row.Property(x => x.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        row.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TargetCreationPermits_Ids", "[PermitId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000' AND [AttemptId] <> '00000000-0000-0000-0000-000000000000'");
            t.HasCheckConstraint("CK_TargetCreationPermits_ExpectedOperationRevision", "[ExpectedOperationRevision] = 1");
            t.HasCheckConstraint("CK_TargetCreationPermits_NonExecutable", "[Executable] = 0");
            t.HasCheckConstraint("CK_TargetCreationPermits_JobsBlocked", "[ScheduledJobsMustRemainBlocked] = 1");
            // DATEADD overflow rejects an unrepresentable deadline rather than admitting a clamped window.
            t.HasCheckConstraint("CK_TargetCreationPermits_Window", "DATEPART(TZOFFSET, [IssuedAt]) = 0 AND DATEPART(TZOFFSET, [StartBefore]) = 0 AND DATEADD(minute, 5, [IssuedAt]) = [StartBefore]");
            t.HasCheckConstraint("CK_TargetCreationPermits_StartedAt", "[StartedAt] IS NULL OR (DATEPART(TZOFFSET, [StartedAt]) = 0 AND [StartedAt] >= [IssuedAt] AND [StartedAt] < [StartBefore])");
            t.HasCheckConstraint("CK_TargetCreationPermits_RevisionState", "([State] = 'Prepared' AND [Revision] = 1 AND [StartedAt] IS NULL AND [StartExpectedRevision] IS NULL) OR ([State] = 'StartRecorded' AND [Revision] = 2 AND [StartedAt] IS NOT NULL AND [StartExpectedRevision] IS NOT NULL AND [StartExpectedRevision] = 1) OR ([State] = 'Revoked' AND (([Revision] = 2 AND [StartedAt] IS NULL AND [StartExpectedRevision] IS NULL) OR ([Revision] = 3 AND [StartedAt] IS NOT NULL AND [StartExpectedRevision] IS NOT NULL AND [StartExpectedRevision] = 1)))");
        });
    }

    private void GuardCreationPermits()
    {
        foreach (var entry in ChangeTracker.Entries<CreationPermitRecord>())
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Creation permits cannot be deleted.");
            if (entry.State is not (EntityState.Added or EntityState.Modified))
                continue;

            var current = entry.Entity;
            if (current.PermitId == Guid.Empty || current.OperationId == Guid.Empty || current.AttemptId == Guid.Empty ||
                current.ExpectedOperationRevision != 1 || current.Executable || !current.ScheduledJobsMustRemainBlocked ||
                current.IssuedAt.Offset != TimeSpan.Zero || current.StartBefore.Offset != TimeSpan.Zero ||
                current.IssuedAt > DateTimeOffset.MaxValue.AddMinutes(-5) || current.StartBefore != current.IssuedAt.AddMinutes(5) ||
                current.StartedAt is { } started && (started.Offset != TimeSpan.Zero || started < current.IssuedAt || started >= current.StartBefore))
                throw new InvalidOperationException("Creation permit anchors, safety flags and timestamps are invalid.");

            if (entry.State == EntityState.Added)
            {
                if (current.State != TargetCreationPermitState.Prepared || current.Revision != 1 ||
                    current.StartedAt is not null || current.StartExpectedRevision is not null)
                    throw new InvalidOperationException("Creation permits must be inserted in the prepared revision-one state.");
                continue;
            }

            if (entry.Properties.Any(p => p.IsModified && p.Metadata.Name is not
                (nameof(CreationPermitRecord.State) or nameof(CreationPermitRecord.Revision) or
                 nameof(CreationPermitRecord.StartedAt) or nameof(CreationPermitRecord.StartExpectedRevision))))
                throw new InvalidOperationException("Creation permit anchors and safety flags are immutable.");

            var original = (CreationPermitRecord)entry.OriginalValues.ToObject();
            var fromPrepared = original.State == TargetCreationPermitState.Prepared && original.Revision == 1 &&
                original.StartedAt is null && original.StartExpectedRevision is null;
            var fromStarted = original.State == TargetCreationPermitState.StartRecorded && original.Revision == 2 &&
                original.StartedAt is not null && original.StartExpectedRevision == 1;
            var toStarted = fromPrepared && current.State == TargetCreationPermitState.StartRecorded &&
                current.Revision == 2 && current.StartedAt is not null && current.StartExpectedRevision == 1;
            var toRevoked = (fromPrepared || fromStarted) && current.State == TargetCreationPermitState.Revoked &&
                current.Revision == original.Revision + 1 && current.StartedAt == original.StartedAt &&
                current.StartExpectedRevision == original.StartExpectedRevision;
            if (!(toStarted || toRevoked))
                throw new InvalidOperationException("Creation permit transition is not permitted.");
        }
    }
}
