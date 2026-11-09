using BuildSystem.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BuildSystem.Infrastructure;

public sealed partial class BuildSystemDbContext
{
    public DbSet<TargetCreationExecutionClaim> TargetCreationExecutionClaims => Set<TargetCreationExecutionClaim>();
    private static void ConfigureCreationExecutionClaimModel(ModelBuilder b)
    {
        var row = b.Entity<TargetCreationExecutionClaim>();
        row.HasKey(x => x.ClaimId);
        row.Property(x => x.ClaimId).ValueGeneratedNever();
        row.HasIndex(x => x.PermitId).IsUnique();
        row.HasIndex(x => x.OperationId).IsUnique();
        row.HasIndex(x => x.AttemptId).IsUnique();
        row.HasOne<CreationPermitRecord>().WithMany().HasForeignKey(x => x.PermitId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<CreationOperationRecord>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<TargetCredentialRecord>().WithMany().HasForeignKey(x => x.CredentialId).OnDelete(DeleteBehavior.Restrict);
        foreach (var property in row.Metadata.GetProperties()) property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        row.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TargetCreationExecutionClaims_Ids", "[ClaimId] <> '00000000-0000-0000-0000-000000000000' AND [PermitId] <> '00000000-0000-0000-0000-000000000000' AND [OperationId] <> '00000000-0000-0000-0000-000000000000' AND [AttemptId] <> '00000000-0000-0000-0000-000000000000' AND [AgentId] <> '00000000-0000-0000-0000-000000000000' AND [CredentialId] <> '00000000-0000-0000-0000-000000000000' AND [EndpointMappingId] <> '00000000-0000-0000-0000-000000000000'");
            t.HasCheckConstraint("CK_TargetCreationExecutionClaims_Window", "DATEPART(TZOFFSET, [IssuedAt]) = 0 AND DATEPART(TZOFFSET, [ExpiresAt]) = 0 AND [IssuedAt] < [ExpiresAt] AND [IssuedAt] >= DATEADD(minute, -5, [ExpiresAt])");
        });
    }
    private void GuardCreationExecutionClaims()
    {
        foreach (var entry in ChangeTracker.Entries<TargetCreationExecutionClaim>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Execution claims are append-only.");
            if (entry.State != EntityState.Added) continue;
            var row = entry.Entity;
            if (new[] { row.ClaimId, row.PermitId, row.OperationId, row.AttemptId, row.AgentId, row.CredentialId, row.EndpointMappingId }.Any(x => x == Guid.Empty) ||
                row.IssuedAt.Offset != TimeSpan.Zero || row.ExpiresAt.Offset != TimeSpan.Zero || row.IssuedAt >= row.ExpiresAt ||
                row.ExpiresAt < DateTimeOffset.MinValue.AddMinutes(5) || row.IssuedAt < row.ExpiresAt.AddMinutes(-5))
                throw new InvalidOperationException("Invalid execution claim anchors.");
        }
    }
}