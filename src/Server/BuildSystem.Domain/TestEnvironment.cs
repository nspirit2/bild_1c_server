namespace BuildSystem.Domain;

/// <summary>
/// Pure lifecycle aggregate, not a repository or an external operation executor.
/// Version checks reject stale commands on this instance only. Persistence MUST use a DB
/// concurrency token/conditional update and a transaction; this object supplies no thread safety.
/// </summary>
public sealed class TestEnvironment
{
    public TestEnvironment(Guid id, EnvironmentReservation reservation, LifetimePolicy lifetime,
        VerificationRequirements verification, DateTimeOffset requestedAt)
    {
        Guard.Id(id);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(verification);
        Guard.Utc(requestedAt);
        Id = id;
        Reservation = reservation;
        Lifetime = lifetime;
        Verification = verification;
        RequestedAt = requestedAt;
        UpdatedAt = requestedAt;
    }

    public Guid Id { get; }
    public EnvironmentReservation Reservation { get; }
    public LifetimePolicy Lifetime { get; }
    public VerificationRequirements Verification { get; }
    public EnvironmentState State { get; private set; } = EnvironmentState.Requested;
    public long Version { get; private set; } = 1;
    public DateTimeOffset RequestedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ReadyAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? DeleteAfter { get; private set; }
    public Guid? ProvisioningJobId { get; private set; }
    public int? ProvisioningAttempt { get; private set; }
    public Guid? DeletionJobId { get; private set; }
    public string? DiagnosticCode { get; private set; }
    public VerificationEvidence? LastVerification { get; private set; }
    public DeletionEvidence? LastDeletion { get; private set; }
    public bool HoldsQuota => State != EnvironmentState.Deleted;

    public void StartProvisioning(Guid jobId, int attempt, DateTimeOffset now)
    {
        At(now);
        In(EnvironmentState.Requested);
        Guard.Id(jobId);
        Guard.Require(attempt > 0, "InvalidAttempt", "Attempt must be positive.");
        ProvisioningJobId = jobId;
        ProvisioningAttempt = attempt;
        Move(EnvironmentState.Provisioning, now);
    }

    public void StartPreparing(DateTimeOffset now)
    {
        At(now);
        In(EnvironmentState.Provisioning);
        Move(EnvironmentState.Preparing, now);
    }

    public void PublishReady(VerificationEvidence evidence, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        At(now);
        In(EnvironmentState.Preparing);
        Guard.Utc(evidence.ObservedAt);
        Guard.Require(evidence.ObservedAt >= UpdatedAt && evidence.ObservedAt <= now,
            "StaleEvidence", "Verify must follow preparation and cannot be in the future.");
        Guard.Require(evidence.ReservationId == Reservation.Id &&
            evidence.JobId == ProvisioningJobId && evidence.Attempt == ProvisioningAttempt &&
            evidence.SqlDatabase == Reservation.SqlDatabase && evidence.Infobase == Reservation.Infobase,
            "EvidenceMismatch", "Verify must match reservation resources and current job attempt.");
        Guard.Require(evidence.SourceFingerprint == Verification.SourceFingerprint &&
            evidence.ConfigurationVersion == Verification.ConfigurationVersion &&
            evidence.ProfileVersion == Verification.ProfileVersion,
            "EvidenceMismatch", "Verify must match pinned source, configuration and profile.");
        var mandatory = new[] { evidence.ResourceOwnership, evidence.NoConflictingOperation,
            evidence.Connection, evidence.ProfilePostconditions, evidence.OwnerAccess,
            evidence.ImportedAccessDisabled, evidence.InfrastructureIsolation, evidence.IntegrationSettings,
            evidence.ScheduledJobsSuppressedThroughoutRestore };
        Guard.Require(mandatory.All(x => x == CheckOutcome.Passed) &&
            (Verification.Kind == EnvironmentKind.Empty ||
             (evidence.ApplicationControlData == CheckOutcome.Passed &&
              evidence.SafeApplicationScenario == CheckOutcome.Passed)),
            "VerificationIncomplete", "All applicable structured checks must pass.");
        var expires = ExpiresAt ?? now.Add(Lifetime.InitialTtl);
        var deleteAfter = DeletionDeadline(expires);
        LastVerification = evidence;
        ReadyAt ??= now;
        ExpiresAt = expires;
        DeleteAfter = deleteAfter;
        DiagnosticCode = null;
        Move(now >= expires ? EnvironmentState.Expired : EnvironmentState.Ready, now);
    }

    public void Fail(string diagnosticCode, DateTimeOffset now)
    {
        At(now);
        In(EnvironmentState.Requested, EnvironmentState.Provisioning, EnvironmentState.Preparing);
        Guard.Text(diagnosticCode);
        DiagnosticCode = diagnosticCode;
        Move(EnvironmentState.Failed, now);
    }

    public void RequireAttention(string diagnosticCode, DateTimeOffset now)
    {
        At(now);
        Guard.Require(State is not EnvironmentState.Deleted and not EnvironmentState.NeedsAttention,
            "InvalidTransition", "Cannot mark this state as needing attention.");
        Guard.Text(diagnosticCode);
        DiagnosticCode = diagnosticCode;
        Move(EnvironmentState.NeedsAttention, now);
    }

    /// <summary>Only reconciliation may authorize another verification; never direct NeedsAttention to Ready.</summary>
    public void ResumeVerification(Guid jobId, int attempt, DateTimeOffset now)
    {
        At(now);
        In(EnvironmentState.NeedsAttention);
        Guard.Require(DeletionJobId is null, "DeletionStarted", "Deletion cannot be undone by verification.");
        Guard.Id(jobId);
        Guard.Require(attempt > 0, "InvalidAttempt", "Attempt must be positive.");
        ProvisioningJobId = jobId;
        ProvisioningAttempt = attempt;
        Move(EnvironmentState.Preparing, now);
    }

    public bool Expire(long expectedVersion, DateTimeOffset now)
    {
        Expected(expectedVersion);
        At(now);
        if (State == EnvironmentState.Expired) return false;
        In(EnvironmentState.Ready);
        if (now < ExpiresAt) return false;
        Move(EnvironmentState.Expired, now);
        return true;
    }

    /// <summary>Caller rechecks current quota policy in the same DB transaction as this change.</summary>
    public void Extend(long expectedVersion, TimeSpan durationFromNow, DateTimeOffset now)
    {
        Expected(expectedVersion);
        At(now);
        In(EnvironmentState.Ready, EnvironmentState.Expired);
        Guard.Require(durationFromNow > TimeSpan.Zero && durationFromNow <= Lifetime.MaxExtension,
            "InvalidTtl", "Extension is bounded from the time of extension, not the old deadline.");
        var expires = now.Add(durationFromNow);
        Guard.Require(expires > ExpiresAt, "NotAnExtension", "Extension must increase the deadline.");
        var deleteAfter = DeletionDeadline(expires);
        ExpiresAt = expires;
        DeleteAfter = deleteAfter;
        Move(EnvironmentState.Ready, now);
    }

    /// <summary>Authorizes a job intent only. Authentication/data-loss consent and DB locks are caller duties.</summary>
    public Guid BeginDeletion(long expectedVersion, Guid jobId, DeletionSafetyEvidence safety, DateTimeOffset now)
    {
        Expected(expectedVersion);
        At(now);
        ArgumentNullException.ThrowIfNull(safety);
        Guard.Id(jobId);
        if (State is EnvironmentState.Deleting or EnvironmentState.Deleted) return DeletionJobId!.Value;
        In(EnvironmentState.Requested, EnvironmentState.Failed, EnvironmentState.Ready,
            EnvironmentState.Expired, EnvironmentState.NeedsAttention);
        Guard.Utc(safety.ObservedAt);
        Guard.Text(safety.JournalReference);
        Guard.Require(safety.ReservationId == Reservation.Id &&
            safety.SqlDatabase == Reservation.SqlDatabase && safety.Infobase == Reservation.Infobase &&
            safety.ObservedAt >= UpdatedAt && safety.ObservedAt <= now,
            "EvidenceMismatch", "Deletion safety must match current reservation and state.");
        Guard.Require(safety.OwnershipAndCreationHistory == CheckOutcome.Passed &&
            safety.NoConflictingOperation == CheckOutcome.Passed && safety.SessionsDrained == CheckOutcome.Passed,
            "UnsafeDeletion", "Ownership, settled operations and drained sessions are mandatory.");
        DeletionJobId = jobId;
        Move(EnvironmentState.Deleting, now);
        return DeletionJobId.Value;
    }

    public void ConfirmDeleted(DeletionEvidence evidence, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        At(now);
        In(EnvironmentState.Deleting);
        Guard.Utc(evidence.ObservedAt);
        Guard.Text(evidence.JournalReference);
        Guard.Require(evidence.ReservationId == Reservation.Id && evidence.DeletionJobId == DeletionJobId &&
            evidence.SqlDatabase == Reservation.SqlDatabase && evidence.Infobase == Reservation.Infobase &&
            evidence.ObservedAt >= UpdatedAt && evidence.ObservedAt <= now,
            "EvidenceMismatch", "Deletion confirmation must match current resources and deletion job.");
        Guard.Require(evidence.OwnershipAndCreationHistory == CheckOutcome.Passed &&
            evidence.NoConflictingOperation == CheckOutcome.Passed &&
            evidence.SqlDatabaseAbsent == CheckOutcome.Passed && evidence.InfobaseAbsent == CheckOutcome.Passed,
            "DeletionUnconfirmed", "Both external resources must be confirmed absent before releasing quota.");
        LastDeletion = evidence;
        Move(EnvironmentState.Deleted, now);
    }

    private DateTimeOffset? DeletionDeadline(DateTimeOffset expires) =>
        Lifetime.AutomaticDeletionGrace is { } grace ? expires.Add(grace) : null;

    private void Expected(long expected) => Guard.Require(expected == Version,
        "VersionConflict", "The expected version is stale.");
    private void At(DateTimeOffset now)
    {
        Guard.Utc(now);
        Guard.Require(now >= UpdatedAt, "TimeWentBackwards", "Time must not precede the last transition.");
    }
    private void In(params EnvironmentState[] states) => Guard.Require(states.Contains(State),
        "InvalidTransition", $"Operation is not permitted in {State}.");
    private void Move(EnvironmentState state, DateTimeOffset now)
    {
        State = state;
        UpdatedAt = now;
        Version++;
    }
}
