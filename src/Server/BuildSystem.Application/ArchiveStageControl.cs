using BuildSystem.Contracts;

namespace BuildSystem.Application;

/// <summary>Separate stage-only machine protocol. Never grants restore or completes provisioning.</summary>
public interface IArchiveStageControl
{
    Task<StageLeaseGrant?> PollAsync(string credential, CancellationToken ct);
    Task<LeaseStatus> HeartbeatAsync(string credential, LeaseHeartbeat heartbeat, CancellationToken ct);
}

/// <summary>Deployment opt-in; false unless explicitly enabled by an administrator.</summary>
public sealed class ArchiveStageOptions
{
    public bool Enabled { get; init; }
}
// end
