using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

public sealed class AgentEnrollment
{
    public Guid Id { get; set; }
    public Guid ExpectedAgentId { get; set; }
    public string ExpectedName { get; set; } = "";
    public Guid PoolId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public sealed class AgentRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid PoolId { get; set; }
    public string State { get; set; } = "Pending";
    public string? TokenHash { get; set; }
    public bool SupportsControlPlaneProbe { get; set; }
    public DateTimeOffset? LastHeartbeat { get; set; }
}

public sealed class AgentLease
{
    // One durable lease history row per job; fencing never resets, including after terminal outcomes.
    public Guid JobId { get; set; }
    public Guid AgentId { get; set; }
    public long FencingToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string State { get; set; } = "Active";
}

public sealed class AgentEvent
{
    public Guid EventId { get; set; }
    public Guid AgentId { get; set; }
    public Guid JobId { get; set; }
    public long FencingToken { get; set; }
    public string Code { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed partial class BuildSystemDbContext
{
    public DbSet<AgentEnrollment> AgentEnrollments => Set<AgentEnrollment>();
    public DbSet<AgentRecord> Agents => Set<AgentRecord>();
    public DbSet<AgentLease> AgentLeases => Set<AgentLease>();
    public DbSet<AgentEvent> AgentEvents => Set<AgentEvent>();

    private static void ConfigureAgentModel(ModelBuilder b)
    {
        b.Entity<AgentEnrollment>().Property(x => x.TokenHash).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2");
        b.Entity<AgentEnrollment>().Property(x => x.ExpectedName).HasMaxLength(120).UseCollation("Latin1_General_100_BIN2");
        b.Entity<AgentEnrollment>().HasOne<EnvironmentPool>().WithMany().HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AgentRecord>().ToTable("Agents");
        b.Entity<AgentRecord>().Property(x => x.Name).HasMaxLength(120);
        b.Entity<AgentRecord>().Property(x => x.State).HasMaxLength(32);
        b.Entity<AgentRecord>().Property(x => x.TokenHash).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2");
        b.Entity<AgentRecord>().HasIndex(x => x.TokenHash).IsUnique().HasFilter("[TokenHash] IS NOT NULL");
        b.Entity<AgentRecord>().HasOne<EnvironmentPool>().WithMany().HasForeignKey(x => x.PoolId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AgentLease>().HasKey(x => x.JobId);
        b.Entity<AgentLease>().Property(x => x.State).HasMaxLength(32);
        b.Entity<AgentLease>().HasIndex(x => new { x.AgentId, x.State });
        b.Entity<AgentLease>().HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AgentLease>().HasOne<EnvironmentJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AgentEvent>().HasKey(x => x.EventId);
        b.Entity<AgentEvent>().Property(x => x.Code).HasMaxLength(32);
        b.Entity<AgentEvent>().Property(x => x.PayloadHash).HasMaxLength(64);
        b.Entity<AgentEvent>().HasOne<AgentRecord>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<AgentEvent>().HasOne<EnvironmentJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
    }
}
// End of agent persistence model.
