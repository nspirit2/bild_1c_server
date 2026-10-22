namespace BuildSystem.Domain;

public enum EnvironmentState
{
    Requested, Provisioning, Preparing, Ready, Failed, Expired, Deleting, Deleted, NeedsAttention
}

public enum EnvironmentKind { Empty, Restored }
public enum CheckOutcome { Passed, Failed, Unknown }

public sealed class DomainRuleException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal static class Guard
{
    public static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new DomainRuleException(code, message);
    }

    public static void Utc(DateTimeOffset value) =>
        Require(value.Offset == TimeSpan.Zero, "UtcRequired", "Time must have a zero UTC offset.");

    public static void Text(string value) => ArgumentException.ThrowIfNullOrWhiteSpace(value);
    public static void Id(Guid value) => Require(value != Guid.Empty, "InvalidId", "An identifier is required.");
}

/// <summary>
/// Identity of a preallocated resource slot (server/cluster + unique planned resource identifier).
/// It is NOT proof that an external SQL DB/1C registration exists or belongs to the reservation.
/// Actual external incarnation IDs and creation history may be learned only after create; the durable
/// resource journal must bind those observations to this slot. Trusted adapters must inspect that journal
/// and the real resources before asserting ownership in verification/deletion evidence. A name alone,
/// even with a bs_test_ prefix, never establishes ownership.
/// </summary>
public sealed record ResourceIdentity
{
    public ResourceIdentity(string authority, string externalId)
    {
        Guard.Text(authority);
        Guard.Text(externalId);
        Authority = authority;
        ExternalId = externalId;
    }
    public string Authority { get; }
    public string ExternalId { get; }
}

/// <summary>Immutable allocation. No release flag: only a confirmed Deleted environment releases quota.</summary>
public sealed record EnvironmentReservation
{
    public EnvironmentReservation(Guid id, Guid ownerId, Guid poolId, long reservedBytes,
        ResourceIdentity sqlDatabase, ResourceIdentity infobase)
    {
        Guard.Id(id);
        Guard.Id(ownerId);
        Guard.Id(poolId);
        Guard.Require(reservedBytes > 0, "InvalidBudget", "Reserved bytes must be positive.");
        ArgumentNullException.ThrowIfNull(sqlDatabase);
        ArgumentNullException.ThrowIfNull(infobase);
        Id = id;
        OwnerId = ownerId;
        PoolId = poolId;
        ReservedBytes = reservedBytes;
        SqlDatabase = sqlDatabase;
        Infobase = infobase;
    }
    public Guid Id { get; }
    public Guid OwnerId { get; }
    public Guid PoolId { get; }
    public long ReservedBytes { get; }
    public ResourceIdentity SqlDatabase { get; }
    public ResourceIdentity Infobase { get; }
}

public sealed record LifetimePolicy
{
    public LifetimePolicy(TimeSpan initialTtl, TimeSpan maxExtension, TimeSpan? automaticDeletionGrace = null)
    {
        Guard.Require(initialTtl > TimeSpan.Zero && maxExtension > TimeSpan.Zero,
            "InvalidTtl", "TTL limits must be positive.");
        Guard.Require(automaticDeletionGrace is null || automaticDeletionGrace >= TimeSpan.Zero,
            "InvalidGrace", "Deletion grace cannot be negative.");
        InitialTtl = initialTtl;
        MaxExtension = maxExtension;
        AutomaticDeletionGrace = automaticDeletionGrace;
    }
    public TimeSpan InitialTtl { get; }
    public TimeSpan MaxExtension { get; }
    public TimeSpan? AutomaticDeletionGrace { get; }
}

/// <summary>Immutable source/profile expectations fixed before provisioning. Empty has no application promise.</summary>
public sealed record VerificationRequirements
{
    public VerificationRequirements(EnvironmentKind kind, string sourceFingerprint, string configurationVersion,
        string profileVersion)
    {
        Guard.Require(Enum.IsDefined(kind), "InvalidKind", "Unsupported environment kind.");
        Guard.Text(sourceFingerprint);
        Guard.Text(configurationVersion);
        Guard.Text(profileVersion);
        Kind = kind;
        SourceFingerprint = sourceFingerprint;
        ConfigurationVersion = configurationVersion;
        ProfileVersion = profileVersion;
    }
    public EnvironmentKind Kind { get; }
    public string SourceFingerprint { get; }
    public string ConfigurationVersion { get; }
    public string ProfileVersion { get; }
}

/// <summary>Produced by trusted adapters, not client input; outcomes are claims, not physical proof by this library.</summary>
public sealed record VerificationEvidence(
    Guid ReservationId, Guid JobId, int Attempt, DateTimeOffset ObservedAt,
    ResourceIdentity SqlDatabase, ResourceIdentity Infobase,
    string SourceFingerprint, string ConfigurationVersion, string ProfileVersion,
    CheckOutcome ResourceOwnership, CheckOutcome NoConflictingOperation,
    CheckOutcome Connection, CheckOutcome ProfilePostconditions,
    CheckOutcome OwnerAccess, CheckOutcome ImportedAccessDisabled,
    CheckOutcome InfrastructureIsolation, CheckOutcome IntegrationSettings,
    CheckOutcome ScheduledJobsSuppressedThroughoutRestore,
    CheckOutcome ApplicationControlData, CheckOutcome SafeApplicationScenario);

/// <summary>
/// Ownership includes the durable creation/absence history and resource incarnation, not a name prefix.
/// ConfirmedAbsence may prove a resource was never created after failed provisioning.
/// </summary>
public sealed record DeletionSafetyEvidence(
    Guid ReservationId, ResourceIdentity SqlDatabase, ResourceIdentity Infobase,
    DateTimeOffset ObservedAt, string JournalReference,
    CheckOutcome OwnershipAndCreationHistory, CheckOutcome NoConflictingOperation,
    CheckOutcome SessionsDrained);

public sealed record DeletionEvidence(
    Guid ReservationId, Guid DeletionJobId, DateTimeOffset ObservedAt,
    ResourceIdentity SqlDatabase, ResourceIdentity Infobase, string JournalReference,
    CheckOutcome OwnershipAndCreationHistory, CheckOutcome NoConflictingOperation,
    CheckOutcome SqlDatabaseAbsent, CheckOutcome InfobaseAbsent);

/// <summary>Policy decision only; callers must read and mutate quotas transactionally in the database.</summary>
public sealed record QuotaDecision(bool Allowed, string? RejectionCode)
{
    public static QuotaDecision Permit { get; } = new(true, null);
    public static QuotaDecision Reject(string code) => new(false, code);
}
