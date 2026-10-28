using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

public sealed class BuildSystemUser : IdentityUser<Guid>;

public sealed class Project { public Guid Id { get; set; } public string Name { get; set; } = ""; public bool Enabled { get; set; } = true; public string PreparationProfileVersion { get; set; } = ""; public string IsolationPolicyId { get; set; } = ""; }
public sealed class ProjectMembership { public Guid UserId { get; set; } public Guid ProjectId { get; set; } public string Role { get; set; } = "Developer"; }
public sealed class ArchiveRoot { public Guid Id { get; set; } public bool Enabled { get; set; } public Guid AgentScopeId { get; set; } public string IsolationPolicyId { get; set; } = ""; public long MaxArchiveBytes { get; set; } public long MaxStagingBytes { get; set; } }
public sealed class ArchiveRootProject { public Guid RootId { get; set; } public Guid ProjectId { get; set; } }
public sealed class DiskArchive { public Guid Id { get; set; } public Guid RootId { get; set; } public string RelativePath { get; set; } = ""; }
public sealed class DiskArchiveVersion { public Guid Id { get; set; } public Guid ArchiveId { get; set; } public string Sha256 { get; set; } = ""; public long SizeBytes { get; set; } public string Format { get; set; } = ""; public string State { get; set; } = "Unavailable"; public long EstimatedDbBytes { get; set; } }
public sealed class EnvironmentPool { public bool SupportsDt { get; set; } public bool SupportsBak { get; set; } public bool IsolationVerified { get; set; } public bool ScheduledJobsSuppressionVerified { get; set; } public bool ResourceLimitVerified { get; set; } public Guid Id { get; set; } public Guid ClusterId { get; set; } public Guid SqlServerId { get; set; } public Guid AgentScopeId { get; set; } public bool Enabled { get; set; } public string IsolationPolicyId { get; set; } = ""; public long CapacityBytes { get; set; } public long MaxEnvironmentBytes { get; set; } public long MaxStagingBytes { get; set; } public int MaxConcurrentProvisioning { get; set; } }
public sealed class ProjectPool { public Guid ProjectId { get; set; } public Guid PoolId { get; set; } public bool Enabled { get; set; } }
public sealed class EnvironmentQuota { public Guid SubjectId { get; set; } public int MaxActiveEnvironments { get; set; } public int MaxConcurrentProvisioning { get; set; } public long MaxStorageBytes { get; set; } public int MaxTtlDays { get; set; } }
public sealed class EnvironmentRequest { public Guid Id { get; set; } public Guid OwnerId { get; set; } public Guid ProjectId { get; set; } public Guid EnvironmentId { get; set; } public Guid JobId { get; set; } public Guid ArchiveVersionId { get; set; } public string IdempotencyKey { get; set; } = ""; public string PayloadHash { get; set; } = ""; public string DisplayName { get; set; } = ""; public string State { get; set; } = "Pending"; public int TtlDays { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class EnvironmentReservation { public Guid Id { get; set; } public Guid OwnerId { get; set; } public Guid PoolId { get; set; } public Guid SqlServerId { get; set; } public Guid ClusterId { get; set; } public string SqlDbName { get; set; } = ""; public string InfobaseName { get; set; } = ""; public long ReservedBytes { get; set; } public long StagingBytes { get; set; } public string State { get; set; } = "Held"; }
public sealed class EnvironmentRecord { public bool HasUnsettledProvisioningOperation { get; set; } = true; public Guid Id { get; set; } public Guid RequestId { get; set; } public Guid OwnerId { get; set; } public Guid ProjectId { get; set; } public Guid PoolId { get; set; } public Guid ReservationId { get; set; } public string DisplayName { get; set; } = ""; public string State { get; set; } = "Requested"; public DateTimeOffset? ExpiresAt { get; set; } public byte[] RowVersion { get; set; } = []; }
public sealed class EnvironmentJob { public Guid Id { get; set; } public Guid RequestId { get; set; } public Guid EnvironmentId { get; set; } public string Kind { get; set; } = "ProvisionArchive"; public string State { get; set; } = "Pending"; public string SnapshotJson { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } }
public sealed class AuditEvent { public Guid Id { get; set; } public Guid ActorId { get; set; } public string Action { get; set; } = ""; public Guid TargetId { get; set; } public DateTimeOffset CreatedAt { get; set; } }

public sealed partial class BuildSystemDbContext(DbContextOptions<BuildSystemDbContext> options)
    : IdentityDbContext<BuildSystemUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMembership> ProjectMemberships => Set<ProjectMembership>();
    public DbSet<ArchiveRoot> ArchiveRoots => Set<ArchiveRoot>();
    public DbSet<ArchiveRootProject> ArchiveRootProjects => Set<ArchiveRootProject>();
    public DbSet<DiskArchive> DiskArchives => Set<DiskArchive>();
    public DbSet<DiskArchiveVersion> DiskArchiveVersions => Set<DiskArchiveVersion>();
    public DbSet<EnvironmentPool> EnvironmentPools => Set<EnvironmentPool>();
    public DbSet<ProjectPool> ProjectPools => Set<ProjectPool>();
    public DbSet<EnvironmentQuota> EnvironmentQuotas => Set<EnvironmentQuota>();
    public DbSet<EnvironmentRequest> EnvironmentRequests => Set<EnvironmentRequest>();
    public DbSet<EnvironmentReservation> EnvironmentReservations => Set<EnvironmentReservation>();
    public DbSet<EnvironmentRecord> TestEnvironments => Set<EnvironmentRecord>();
    public DbSet<EnvironmentJob> EnvironmentJobs => Set<EnvironmentJob>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardImmutableSnapshots();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardImmutableSnapshots();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardImmutableSnapshots()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<StageCleanupAuthorizationRecord>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Stage cleanup authorizations are immutable.");
        if (ChangeTracker.Entries<StageRetirementIntent>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Stage retirement intents are immutable.");
        if (ChangeTracker.Entries<StagedArtifact>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Staged artifact receipts are immutable.");
        foreach (var entry in ChangeTracker.Entries<EnvironmentJob>().Where(x => x.State == EntityState.Modified))
            if (new[] { nameof(EnvironmentJob.SnapshotJson), nameof(EnvironmentJob.Kind), nameof(EnvironmentJob.RequestId), nameof(EnvironmentJob.EnvironmentId) }
                .Any(name => entry.Property(name).IsModified))
                throw new InvalidOperationException("Provisioning intent snapshots are immutable.");
        foreach (var entry in ChangeTracker.Entries<DiskArchiveVersion>().Where(x => x.State == EntityState.Modified))
            if (new[] { nameof(DiskArchiveVersion.ArchiveId), nameof(DiskArchiveVersion.Sha256), nameof(DiskArchiveVersion.SizeBytes), nameof(DiskArchiveVersion.Format), nameof(DiskArchiveVersion.EstimatedDbBytes) }
                .Any(name => entry.Property(name).IsModified))
                throw new InvalidOperationException("Archive versions are immutable; publish a new version.");
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        ConfigureAgentModel(b);
        ConfigureArtifactModel(b);
        ConfigureStageRetirementModel(b);
        ConfigureStageCleanupModel(b);
        b.Entity<ProjectMembership>().HasKey(x => new { x.UserId, x.ProjectId });
        b.Entity<ArchiveRootProject>().HasKey(x => new { x.RootId, x.ProjectId });
        b.Entity<ProjectPool>().HasKey(x => new { x.ProjectId, x.PoolId });
        b.Entity<EnvironmentQuota>().HasKey(x => x.SubjectId);
        b.Entity<EnvironmentRequest>().HasIndex(x => new { x.OwnerId, x.IdempotencyKey }).IsUnique();
        b.Entity<EnvironmentRequest>().Property(x => x.IdempotencyKey).UseCollation("Latin1_General_100_BIN2").HasMaxLength(128);
        b.Entity<EnvironmentRequest>().Property(x => x.PayloadHash).HasMaxLength(64);
        b.Entity<EnvironmentReservation>().HasIndex(x => new { x.SqlServerId, x.SqlDbName }).IsUnique();
        b.Entity<EnvironmentReservation>().HasIndex(x => new { x.ClusterId, x.InfobaseName }).IsUnique();
        b.Entity<EnvironmentRecord>().HasIndex(x => x.RequestId).IsUnique();
        b.Entity<EnvironmentRecord>().HasIndex(x => x.ReservationId).IsUnique();
        b.Entity<EnvironmentRecord>().Property(x => x.RowVersion).IsRowVersion();
        b.Entity<DiskArchive>().HasIndex(x => new { x.RootId, x.RelativePath }).IsUnique();
        b.Entity<DiskArchiveVersion>().HasIndex(x => new { x.ArchiveId, x.Sha256 }).IsUnique();
        b.Entity<EnvironmentJob>().HasIndex(x => x.RequestId).IsUnique();
        b.Entity<ArchiveRoot>().Property(x => x.IsolationPolicyId).HasMaxLength(128);
        b.Entity<EnvironmentPool>().Property(x => x.IsolationPolicyId).HasMaxLength(128);
        b.Entity<Project>().Property(x => x.IsolationPolicyId).HasMaxLength(128);
        b.Entity<Project>().Property(x => x.PreparationProfileVersion).HasMaxLength(128);
        b.Entity<DiskArchiveVersion>().Property(x => x.Sha256).HasMaxLength(64);
        b.Entity<DiskArchiveVersion>().Property(x => x.Format).HasMaxLength(16);
        b.Entity<EnvironmentReservation>().Property(x => x.SqlDbName).HasMaxLength(128);
        b.Entity<EnvironmentReservation>().Property(x => x.InfobaseName).HasMaxLength(128);
        b.Entity<DiskArchive>().Property(x => x.RelativePath).HasMaxLength(400).UseCollation("Latin1_General_100_CI_AS_KS_WS");
        b.Entity<ProjectMembership>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProjectMembership>().HasOne<BuildSystemUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ArchiveRootProject>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ArchiveRootProject>().HasOne<ArchiveRoot>().WithMany().HasForeignKey(x => x.RootId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DiskArchive>().HasOne<ArchiveRoot>().WithMany().HasForeignKey(x => x.RootId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<DiskArchiveVersion>().HasOne<DiskArchive>().WithMany().HasForeignKey(x => x.ArchiveId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProjectPool>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProjectPool>().HasOne<EnvironmentPool>().WithMany().HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EnvironmentQuota>().HasOne<BuildSystemUser>().WithMany().HasForeignKey(x => x.SubjectId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EnvironmentRequest>().HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EnvironmentRequest>().HasOne<BuildSystemUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EnvironmentRequest>().HasOne<DiskArchiveVersion>().WithMany().HasForeignKey(x => x.ArchiveVersionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EnvironmentReservation>().HasOne<EnvironmentPool>().WithMany().HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EnvironmentReservation>().HasOne<BuildSystemUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EnvironmentRecord>().HasOne<EnvironmentRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EnvironmentRecord>().HasOne<EnvironmentReservation>().WithMany().HasForeignKey(x => x.ReservationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EnvironmentJob>().HasOne<EnvironmentRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EnvironmentJob>().HasOne<EnvironmentRecord>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
        // Request receipt IDs deliberately have no reverse FK: avoid circular insert dependencies.
    }
}
