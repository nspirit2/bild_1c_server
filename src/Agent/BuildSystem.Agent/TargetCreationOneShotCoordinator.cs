using BuildSystem.Agent.Tools;

namespace BuildSystem.Agent;

/// <summary>Trusted configuration, snapshotted before any IO. Enabling does not wire a worker.</summary>
public sealed class TargetCreationOneShotOptions
{
    public bool Enabled { get; init; }
    public Guid TrustedAgentId { get; init; }
    public Guid CanonicalClusterId { get; init; }
    public Guid CanonicalSqlServerId { get; init; }
    public Guid EndpointMappingId { get; init; }
    public Guid RacObservationMappingId { get; init; }
    public Guid SqlObservationMappingId { get; init; }
    public override string ToString() => "TargetCreationOneShotOptions [details omitted]";
}

/// <summary>Trusted exclusive namespace adapter. No production implementation is supplied here.</summary>
public interface ITargetCreationNamespacePort
{
    Task<ITargetCreationNamespaceHold> AcquireAsync(SqlTargetNamespaceScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Must retain physical exclusion until disposal, and reject lost or mismatched holds.</summary>
public interface ITargetCreationNamespaceHold : IAsyncDisposable
{
    SqlTargetNamespaceScope Scope { get; }
    Task CheckHeldAsync(CancellationToken cancellationToken = default);
}

/// <summary>Trusted fresh RAC/SQL observations followed by TargetPresenceAssessment.Assess for the
/// exact caller-generated scope, not a cached Boolean. Secrets are transient managed strings:
/// they cannot be erased and MUST NOT be retained, journaled, serialized or logged.</summary>
public interface ITargetCreationAbsencePort
{
    Task<TargetPresenceAssessmentResult> ObserveAsync(TargetPresenceAssessmentScope expected,
        TargetCreationDeliveredSecretValues secrets, CancellationToken cancellationToken = default);
}

/// <summary>Trusted bounded, cooperative adapter for ONE RacInfobaseCreationInvocation, no retries.
/// Secrets MUST NOT be retained or logged. Completion must include physical process completion;
/// ignoring cancellation does not permit detaching work or releasing the namespace early.
/// The same pre-claim monotonic launch deadline must reach the runner without renewal.
/// No production host registration or physical endpoint mapping is supplied by this slice.</summary>
public interface ITargetCreationSingleInvocation
{
    Task<RacInfobaseCreationOutcome> InvokeAsync(RacInfobaseCreationScope scope,
        TargetCreationDeliveredSecretValues secrets, ProcessLaunchDeadline launchDeadline,
        CancellationToken cancellationToken = default);
}

public enum TargetCreationOneShotOutcome { AlreadyAttempted, ReconciliationRequired }

/// <summary>Inert metadata. InvocationAttempted marks entry to the port, not proof of OS process start.
/// An acknowledgement never establishes successful creation, ownership, or usable targets.</summary>
public sealed record TargetCreationOneShotReceipt(TargetCreationRecord Record, TargetCreationPermitBinding Binding,
    TargetCreationOneShotOutcome Outcome, bool PhysicalInvocationAttempted, bool CreationAcknowledged)
{
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool RetryAllowed => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetCreationOneShotReceipt [NONEXECUTABLE; details omitted]";
}

public sealed class TargetCreationOneShotException : Exception
{
    internal TargetCreationOneShotException(bool unconfirmed = false) : base(unconfirmed
        ? "TargetCreationOneShotReconciliationUnconfirmed" : "TargetCreationOneShotUnavailable") { }
    public string Code => Message;
    public override string ToString() => Code;
}

/// <summary>Isolated, default-off alternative INITIAL orchestration. No CLI, worker, live factory,
/// retry, success transition, or local slot release. The trusted host must serialize other access
/// to this journal as well. Claim freshness uses a conservative monotonic budget at port entry,
/// not wall-clock authority or a guarantee about the later instant of OS process launch.</summary>
public sealed class TargetCreationOneShotCoordinator
{
    private readonly AgentJournal _journal;
    private readonly TargetCreationCredentialPreparation _preparation;
    private readonly ITargetCreationNamespacePort _namespaces;
    private readonly ITargetCreationAbsencePort _absence;
    private readonly ITargetCreationExecutionClaimTransport _claims;
    private readonly ITargetCreationSingleInvocation _invocation;
    private readonly TimeProvider _clock;
    private readonly bool _enabled;
    private readonly Guid _agent, _cluster, _sql, _mapping, _racObservation, _sqlObservation;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TargetCreationOneShotCoordinator(AgentJournal journal, TargetCreationCredentialPreparation preparation,
        ITargetCreationNamespacePort namespacePort, ITargetCreationAbsencePort absencePort,
        ITargetCreationExecutionClaimTransport claimTransport, ITargetCreationSingleInvocation invocationPort,
        TargetCreationOneShotOptions options, TimeProvider? timeProvider = null)
    {
        if (journal is null || preparation is null || namespacePort is null || absencePort is null ||
            claimTransport is null || invocationPort is null || options is null)
            throw new TargetCreationOneShotException();
        _enabled = options.Enabled;
        _agent = options.TrustedAgentId; _cluster = options.CanonicalClusterId; _sql = options.CanonicalSqlServerId;
        _mapping = options.EndpointMappingId; _racObservation = options.RacObservationMappingId;
        _sqlObservation = options.SqlObservationMappingId;
        if (_enabled && new[] { _agent, _cluster, _sql, _mapping, _racObservation, _sqlObservation }.Any(id => id == Guid.Empty))
            throw new TargetCreationOneShotException();
        _journal = journal; _preparation = preparation; _namespaces = namespacePort; _absence = absencePort;
        _claims = claimTransport; _invocation = invocationPort; _clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<TargetCreationOneShotReceipt> RunAsync(TargetCreationIntent intent,
        TargetCreationPermitBinding binding, CancellationToken cancellationToken = default)
    {
        if (!_enabled) throw new TargetCreationOneShotException();
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Reject known configuration/caller pin mismatches before consuming the initial attempt.
            if (intent is null || binding is null || binding.AgentId != _agent ||
                intent.CanonicalClusterId != _cluster || intent.CanonicalSqlServerId != _sql ||
                binding.EndpointMappingId != _mapping)
                throw new TargetCreationOneShotException();
            TargetCreationCredentialPreparationReceipt prepared;
            try { prepared = await _preparation.PrepareAsync(intent, binding, cancellationToken).ConfigureAwait(false); }
            catch (TargetCreationCredentialPreparationException e)
            { throw new TargetCreationOneShotException(e.Code == "TargetCreationCredentialPreparationReconciliationUnconfirmed"); }
            // Only a fresh prepared receipt owns the exact revision-2 attempt. Never quarantine a
            // preexisting attempt, or repeat quarantine already performed by preparation.
            if (prepared.Outcome != TargetCreationCredentialPreparationOutcome.CredentialsPrepared)
            {
                using (prepared)
                    return new(prepared.Record, prepared.Binding,
                        prepared.Outcome == TargetCreationCredentialPreparationOutcome.AlreadyAttempted
                            ? TargetCreationOneShotOutcome.AlreadyAttempted : TargetCreationOneShotOutcome.ReconciliationRequired,
                        false, false);
            }
            var begunRecord = prepared.Record;
            var begunBinding = prepared.Binding;
            ITargetCreationNamespaceHold? hold = null;
            var attempted = false;
            var acknowledged = false;
            TargetCreationAcknowledgement? acknowledgement = null;
            try
            {
                if (begunRecord.Intent != intent || begunBinding != binding) throw new InvalidOperationException();
                var bridge = TargetCreationScopeBridge.Prepare(begunRecord, begunBinding, _agent, _cluster,
                    _sql, _mapping, _racObservation, _sqlObservation);
                cancellationToken.ThrowIfCancellationRequested();
                if (_journal.GetTargetCreationIntent(intent.OperationId) != begunRecord ||
                    _journal.GetTargetCreationPermitBinding(intent.OperationId) != begunBinding)
                    throw new InvalidOperationException();
                hold = await _namespaces.AcquireAsync(bridge.NamespaceScope, cancellationToken).ConfigureAwait(false);
                if (hold is null || hold.Scope != bridge.NamespaceScope) throw new InvalidOperationException();
                await hold.CheckHeldAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var request = bridge.PresenceRequest;
                Guid racId;
                do { racId = Guid.NewGuid(); } while (racId == Guid.Empty);
                Guid sqlId;
                do { sqlId = Guid.NewGuid(); } while (sqlId == racId || sqlId == Guid.Empty);
                var expected = new TargetPresenceAssessmentScope(request.OperationId, request.FencingToken,
                    request.TargetIncarnationId, request.ReservationId, request.CanonicalClusterId,
                    request.CanonicalSqlServerId, request.RacEndpointMappingId, request.SqlEndpointMappingId, racId, sqlId);
                var observation = await _absence.ObserveAsync(expected, prepared.ReadSecrets(), cancellationToken).ConfigureAwait(false);
                if (observation is null || observation.Scope != expected || observation.Executable ||
                    !observation.ScheduledJobsMustRemainBlocked || observation.Observation != TargetPresenceObservation.BothObservedAbsent)
                    throw new InvalidOperationException();
                await hold.CheckHeldAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // Lower bound before server issuance: local elapsed time includes the entire exchange.
                var started = _clock.GetTimestamp();
                var grant = await _claims.ClaimAsync(intent, binding, cancellationToken).ConfigureAwait(false);
                if (grant is null || grant.ClaimId == Guid.Empty || grant.TargetIntent != intent || grant.ScopeBinding != binding ||
                    !grant.ServerIssued || !grant.NewlyIssued || !grant.Executable || grant.RetryAllowed ||
                    !grant.ScheduledJobsMustRemainBlocked || grant.IssuedAt < binding.IssuedAt ||
                    grant.ExpiresAt != binding.StartBefore || grant.IssuedAt >= grant.ExpiresAt)
                    throw new InvalidOperationException();
                var budget = grant.ExpiresAt - grant.IssuedAt;
                if (grant.IssuedAt.Offset != TimeSpan.Zero || grant.ExpiresAt.Offset != TimeSpan.Zero ||
                    budget > TimeSpan.FromMinutes(5)) throw new InvalidOperationException();
                void CheckBudget()
                {
                    var elapsed = _clock.GetElapsedTime(started, _clock.GetTimestamp());
                    if (elapsed < TimeSpan.Zero || elapsed >= budget) throw new InvalidOperationException();
                }
                CheckBudget();
                // Construct once from the pre-claim timestamp and the validated server TTL;
                // downstream delivery must not restart this clock or create fresh authority.
                var launchDeadline = new ProcessLaunchDeadline(_clock, started, budget);
                await hold.CheckHeldAsync(cancellationToken).ConfigureAwait(false);
                CheckBudget();
                if (_journal.GetTargetCreationIntent(intent.OperationId) != begunRecord ||
                    _journal.GetTargetCreationPermitBinding(intent.OperationId) != begunBinding)
                    throw new InvalidOperationException();
                _ = TargetCreationLocalPreflight.ValidateInFlight(begunRecord, begunBinding);
                if (hold.Scope != bridge.NamespaceScope) throw new InvalidOperationException();
                var secrets = prepared.ReadSecrets();
                cancellationToken.ThrowIfCancellationRequested();
                CheckBudget();
                attempted = true; // Attempt boundary only; a thrown port may or may not have launched.
                var outcome = await _invocation.InvokeAsync(bridge.CreationScope, secrets, launchDeadline,
                    cancellationToken).ConfigureAwait(false);
                acknowledged = !cancellationToken.IsCancellationRequested && outcome is not null &&
                    outcome.Scope == bridge.CreationScope && !outcome.Executable && !outcome.RetryAllowed &&
                    outcome.ScheduledJobsMustRemainBlocked && outcome.RequiresIndependentVerification &&
                    outcome.Kind == RacInfobaseCreationOutcomeKind.AcknowledgementObserved &&
                    outcome.ReportedInfobaseId is { } reported && reported != Guid.Empty &&
                    outcome.ReasonCode == "CreationAcknowledgementObserved";
                if (acknowledged)
                    acknowledgement = new TargetCreationAcknowledgement(intent, binding, grant.ClaimId,
                        outcome!.ReportedInfobaseId!.Value, grant.IssuedAt, grant.ExpiresAt);
            }
            catch (Exception) { /* Untrusted diagnostics and secrets never escape. No physical retry. */ }
            finally
            {
                // Await completion and cleanup, never detach on cancellation. Cleanup precedes the
                // ONE quarantine so even disposal failure is covered without a second CAS attempt.
                try { if (hold is not null) await hold.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { acknowledged = false; }
                try { prepared.Dispose(); }
                catch (Exception) { acknowledged = false; }
            }
            if (cancellationToken.IsCancellationRequested || acknowledgement is null) acknowledged = false;
            try
            {
                // Only the successful atomic commit makes the acknowledgement durable. On failure,
                // retain the in-flight barrier: no fallback quarantine, adoption, or retry.
                var record = acknowledged && acknowledgement is not null
                    ? _journal.QuarantineTargetCreationWithAcknowledgement(begunRecord.Intent.OperationId,
                        begunRecord.Revision, acknowledgement)
                    : _journal.QuarantineTargetCreation(begunRecord.Intent.OperationId, begunRecord.Revision,
                        cancellationToken.IsCancellationRequested ? "Cancelled" : "OutcomeUnknown");
                return new(record, begunBinding, TargetCreationOneShotOutcome.ReconciliationRequired, attempted, acknowledged);
            }
            catch (Exception) { throw new TargetCreationOneShotException(true); }
        }
        catch (TargetCreationOneShotException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) { throw new TargetCreationOneShotException(); }
        finally { _gate.Release(); }
    }
}
