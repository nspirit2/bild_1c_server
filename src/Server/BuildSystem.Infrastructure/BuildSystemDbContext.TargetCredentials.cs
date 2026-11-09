using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BuildSystem.Infrastructure;

public sealed partial class BuildSystemDbContext
{
    public DbSet<TargetCredentialRecord> TargetCredentials => Set<TargetCredentialRecord>();

    private static void ConfigureTargetCredentials(ModelBuilder b)
    {
        var row = b.Entity<TargetCredentialRecord>();
        row.HasKey(x => x.CredentialId);
        row.Property(x => x.CredentialId).ValueGeneratedNever();
        row.Property(x => x.Ciphertext).HasColumnType("nvarchar(max)").IsRequired();
        row.Property(x => x.State).HasConversion<string>().HasMaxLength(16).IsRequired();
        row.Property(x => x.Revision).IsConcurrencyToken();
        // EF's change tracker blocks changes to immutable fields; database CHECKs enforce
        // values and bounds, but cannot prohibit privileged direct SQL updates to those fields.
        foreach (var name in new[] { nameof(TargetCredentialRecord.CredentialId),
            nameof(TargetCredentialRecord.CanonicalClusterId), nameof(TargetCredentialRecord.CanonicalSqlServerId),
            nameof(TargetCredentialRecord.EndpointMappingId), nameof(TargetCredentialRecord.Ciphertext),
            nameof(TargetCredentialRecord.CreatedAt) })
            row.Property(name).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        row.ToTable(t =>
        {
            t.HasCheckConstraint("CK_TargetCredentials_Ids", "[CredentialId] <> '00000000-0000-0000-0000-000000000000' AND [CanonicalClusterId] <> '00000000-0000-0000-0000-000000000000' AND [CanonicalSqlServerId] <> '00000000-0000-0000-0000-000000000000' AND [EndpointMappingId] <> '00000000-0000-0000-0000-000000000000'");
            t.HasCheckConstraint("CK_TargetCredentials_Ciphertext", "[Ciphertext] IS NOT NULL AND LEN([Ciphertext]) > 0 AND LEN([Ciphertext]) <= 262144 AND DATALENGTH([Ciphertext]) > 0 AND DATALENGTH([Ciphertext]) <= 524288");
            t.HasCheckConstraint("CK_TargetCredentials_Time", "DATEPART(TZOFFSET, [CreatedAt]) = 0 AND ([RevokedAt] IS NULL OR (DATEPART(TZOFFSET, [RevokedAt]) = 0 AND [RevokedAt] >= [CreatedAt]))");
            t.HasCheckConstraint("CK_TargetCredentials_State", "([State] = 'Stored' AND [Revision] = 1 AND [RevokedAt] IS NULL) OR ([State] = 'Revoked' AND [Revision] = 2 AND [RevokedAt] IS NOT NULL)");
        });
    }

    private void GuardTargetCredentials()
    {
        foreach (var entry in ChangeTracker.Entries<TargetCredentialRecord>())
        {
            if (entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Target credentials cannot be deleted.");
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            var current = entry.Entity;
            if (current.CredentialId == Guid.Empty || current.CanonicalClusterId == Guid.Empty ||
                current.CanonicalSqlServerId == Guid.Empty || current.EndpointMappingId == Guid.Empty ||
                string.IsNullOrWhiteSpace(current.Ciphertext) || current.Ciphertext.Length > TargetCredentialStorage.MaxCiphertextCharacters ||
                current.CreatedAt.Offset != TimeSpan.Zero ||
                current.RevokedAt is { } revoked && (revoked.Offset != TimeSpan.Zero || revoked < current.CreatedAt))
                throw new InvalidOperationException("Target credential bounds, binding or timestamp is invalid.");
            if (entry.State == EntityState.Added)
            {
                if (current.State != TargetCredentialState.Stored || current.Revision != 1 || current.RevokedAt is not null)
                    throw new InvalidOperationException("Target credentials must be stored at revision one.");
                continue;
            }
            if (entry.Properties.Any(p => p.IsModified && p.Metadata.Name is not
                (nameof(TargetCredentialRecord.State) or nameof(TargetCredentialRecord.Revision) or nameof(TargetCredentialRecord.RevokedAt))))
                throw new InvalidOperationException("Target credential binding and ciphertext are immutable.");
            var original = (TargetCredentialRecord)entry.OriginalValues.ToObject();
            if (original.State != TargetCredentialState.Stored || original.Revision != 1 || original.RevokedAt is not null ||
                current.State != TargetCredentialState.Revoked || current.Revision != 2 || current.RevokedAt is null)
                throw new InvalidOperationException("Target credential transition is not permitted.");
        }
    }
}
