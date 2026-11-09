using BuildSystem.Application;

namespace BuildSystem.Infrastructure;

/// <summary>EF persistence only; never return or serialize this record. Ciphertext is retained on revocation.</summary>
public sealed class TargetCredentialRecord
{
    public Guid CredentialId { get; set; }
    public Guid CanonicalClusterId { get; set; }
    public Guid CanonicalSqlServerId { get; set; }
    public Guid EndpointMappingId { get; set; }
    public string Ciphertext { get; set; } = "";
    public TargetCredentialState State { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public override string ToString() => "TargetCredentialRecord [redacted]";
}

/// <summary>Shared storage bounds, measured in UTF-16 code units for SQL nvarchar.</summary>
internal static class TargetCredentialStorage
{
    internal const int MaxCiphertextCharacters = 262144;
}
