namespace BuildSystem.Agent;

/// <summary>Historical assertion projected from the actual saved journal only; not authority.</summary>
public sealed record TargetRegistrationEvidenceReport(Guid ClaimId, Guid ReportedInfobaseId,
    Guid RacObservationId, Guid SqlObservationId, Guid RacEndpointMappingId, Guid SqlEndpointMappingId,
    string RegistrationProfileId, string InfobaseName, string SqlServer, string DatabaseName,
    int SqlDatabaseId, string SqlCreateDate, string SqlState)
{
    public override string ToString() => "TargetRegistrationEvidenceReport [historical; details omitted]";
}

/// <summary>Server acceptance metadata, not independently verified ownership or fresh authority.</summary>
public sealed record TargetRegistrationEvidenceDeliveryReceipt(Guid ClaimId, Guid OperationId,
    string Status, DateTimeOffset AcceptedAt)
{
    public bool Executable => false;
    public bool OwnershipEstablished => false;
    public bool RestoreAllowed => false;
    public bool RetryAllowed => false;
    public bool FreshAuthority => false;
    public bool RequiresIndependentVerification => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetRegistrationEvidenceDeliveryReceipt [historical; details omitted]";
}

public interface ITargetRegistrationEvidenceTransport
{
    Task<TargetRegistrationEvidenceDeliveryReceipt> ReportAsync(TargetRegistrationEvidenceReport report,
        CancellationToken cancellationToken);
}

/// <summary>Local durable acknowledgement of historical delivery; never permits physical retry.</summary>
public sealed class TargetRegistrationEvidenceDeliveryAcknowledgement
{
    internal TargetRegistrationEvidenceDeliveryAcknowledgement(Guid operationId,
        TargetRegistrationEvidenceReport report, TargetRegistrationEvidenceDeliveryReceipt receipt)
        => (OperationId, Report, Receipt) = (operationId, report, receipt);
    public Guid OperationId { get; }
    public TargetRegistrationEvidenceReport Report { get; }
    public TargetRegistrationEvidenceDeliveryReceipt Receipt { get; }
    public bool Executable => false;
    public bool OwnershipEstablished => false;
    public bool RestoreAllowed => false;
    public bool RetryAllowed => false;
    public bool FreshAuthority => false;
    public bool RequiresIndependentVerification => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetRegistrationEvidenceDeliveryAcknowledgement [historical; details omitted]";
}

public sealed class TargetRegistrationEvidenceDeliveryUnavailableException : Exception
{
    public TargetRegistrationEvidenceDeliveryUnavailableException() : base("TargetRegistrationEvidenceDeliveryUnavailable") { }
    public string Code => "TargetRegistrationEvidenceDeliveryUnavailable";
    public override string ToString() => Code;
}

/// <summary>One explicit local delivery pass. No automatic transport or physical-operation retry.</summary>
public sealed record TargetRegistrationEvidenceDeliveryResult(Guid OperationId, bool DeliveredThisCall,
    TargetRegistrationEvidenceDeliveryAcknowledgement Acknowledgement)
{
    public bool Executable => false;
    public bool OwnershipEstablished => false;
    public bool RestoreAllowed => false;
    public bool RetryAllowed => false;
    public bool FreshAuthority => false;
    public bool RequiresIndependentVerification => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetRegistrationEvidenceDeliveryResult [historical; details omitted]";
}

public sealed class TargetRegistrationEvidenceDeliveryOptions
{
    public bool Enabled { get; init; }
    public int TotalTimeoutSeconds { get; init; } = 30;
    public override string ToString() => "TargetRegistrationEvidenceDeliveryOptions [details omitted]";
}

public sealed class TargetRegistrationEvidenceDeliveryException : Exception
{
    internal TargetRegistrationEvidenceDeliveryException(string code, bool acknowledgementMayHaveCompleted = false)
        : base(code is "TargetRegistrationEvidenceDeliveryDisabled" or "TargetRegistrationEvidenceDeliveryBusy" or
            "TargetRegistrationEvidenceDeliveryInvalidOptions" or "TargetRegistrationEvidenceDeliveryInvalidRequest" or
            "TargetRegistrationEvidenceDeliveryUnavailable" or "TargetRegistrationEvidenceDeliveryAcknowledgementUnconfirmed"
            ? code : "TargetRegistrationEvidenceDeliveryUnavailable")
        => AcknowledgementMayHaveCompleted = Message == "TargetRegistrationEvidenceDeliveryAcknowledgementUnconfirmed" && acknowledgementMayHaveCompleted;
    public string Code => Message;
    public bool AcknowledgementMayHaveCompleted { get; }
    public override string ToString() => Code;
}
