namespace BuildSystem.Application;

/// <summary>Default-off, internal administrator credential-storage foundation. No execution or transport opt-in.</summary>
public sealed class TargetCredentialOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCredentialOptions [redacted]";
}

/// <summary>Immutable purpose binding. Future callers must explicitly pin a credential ID; no current/active pointer exists.</summary>
public sealed record TargetCredentialBinding(Guid CredentialId, Guid CanonicalClusterId,
    Guid CanonicalSqlServerId, Guid EndpointMappingId);

/// <summary>Internal secret input only. Never place in events, responses or diagnostics.</summary>
public sealed record TargetCredentialInput(string ClusterUser, string ClusterPassword, string SqlUser, string SqlPassword)
{
    public override string ToString() => "TargetCredentialInput [redacted]";
}

public enum TargetCredentialState { Stored, Revoked }

/// <summary>Metadata only. Stored means retained ciphertext, not active selection or an execution grant.</summary>
public sealed record TargetCredentialMetadata(TargetCredentialBinding Binding, TargetCredentialState State,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt)
{
    public bool Executable => false;
    public override string ToString() => "TargetCredentialMetadata [NONEXECUTABLE]";
}

/// <summary>
/// Bounded internal current-administrator port. No secret read, HTTP adapter, resolver, worker delivery,
/// automatic selection or agent authentication integration. Every store uses a fresh credential ID;
/// an existing ID conflicts even for identical input. Multiple IDs may share the same target binding.
/// </summary>
public interface ITargetCredentials
{
    Task<TargetCredentialMetadata> StoreAsync(Actor actor, TargetCredentialBinding binding,
        TargetCredentialInput input, CancellationToken ct);
    Task<TargetCredentialMetadata> RevokeAsync(Actor actor, Guid credentialId, long expectedRevision, CancellationToken ct);
    Task<TargetCredentialMetadata> GetAsync(Actor actor, Guid credentialId, CancellationToken ct);
}
