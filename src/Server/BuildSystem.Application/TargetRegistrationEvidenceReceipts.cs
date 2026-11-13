namespace BuildSystem.Application;

/// <summary>Historical observations only; ClaimId is the stable replay key, not a new execution grant.</summary>
public sealed record ReportTargetRegistrationEvidence(Guid ClaimId, Guid ReportedInfobaseId,
    Guid RacObservationId, Guid SqlObservationId, Guid RacEndpointMappingId, Guid SqlEndpointMappingId,
    string RegistrationProfileId, string InfobaseName, string SqlServer, string DatabaseName,
    int SqlDatabaseId, string SqlCreateDate, string SqlState)
{
    // SqlCreateDate is the provider DateTime's canonical invariant "O" representation;
    // its Kind is preserved, never coerced through DateTimeOffset or converted to UTC.
    public override string ToString() => "ReportTargetRegistrationEvidence [historical evidence; details redacted]";
}

/// <summary>Acceptance is historical evidence only and never establishes physical outcome or authority.</summary>
public sealed record TargetRegistrationEvidenceReceipt(Guid ClaimId, Guid OperationId, string Status,
    DateTimeOffset AcceptedAt)
{
    public bool Executable => false;
    public bool OwnershipEstablished => false;
    public bool RestoreAllowed => false;
    public bool RetryAllowed => false;
    public bool FreshAuthority => false;
    public bool RequiresIndependentVerification => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetRegistrationEvidenceReceipt [NONEXECUTABLE; independent verification required; details redacted]";
}

public interface ITargetRegistrationEvidenceReceipts
{
    Task<TargetRegistrationEvidenceReceipt> ReportAsync(string agentToken,
        ReportTargetRegistrationEvidence report, CancellationToken ct);
}

/// <summary>Historical SQL evidence storage is opt-in; enabling it grants no execution authority.</summary>
public sealed class TargetRegistrationEvidenceReceiptOptions
{
    public bool Enabled { get; set; } = false;
}
