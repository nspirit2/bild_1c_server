using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;

namespace BuildSystem.Agent;

/// <summary>Trusted operator configuration, snapshotted only when enabled. Mappings (including the
/// creation credential-pair mapping) and SQL literal are assertions, not endpoint attestation.</summary>
public sealed class TargetRegistrationEvidenceCoordinatorOptions
{
    public bool Enabled { get; init; } = false;
    public int TotalTimeoutSeconds { get; init; }
    public Guid ExpectedAgentId { get; init; }
    public Guid ExpectedCanonicalClusterId { get; init; }
    public Guid ExpectedCanonicalSqlServerId { get; init; }
    public Guid ExpectedEndpointMappingId { get; init; }
    public Guid RacObservationMappingId { get; init; }
    public Guid SqlObservationMappingId { get; init; }
    public string ExpectedSqlServer { get; init; } = "";
    public override string ToString() => "TargetRegistrationEvidenceCoordinatorOptions [details omitted]";
}

/// <summary>Fixed, secret-free diagnostic; persistence uncertainty never grants authority.</summary>
public sealed class TargetRegistrationEvidenceCoordinatorException : Exception
{
    internal TargetRegistrationEvidenceCoordinatorException(string code, bool persistenceMayHaveCompleted) : base(
        code is "TargetRegistrationEvidenceCoordinatorDisabled" or "TargetRegistrationEvidenceCoordinatorInvalidOptions" or
            "TargetRegistrationEvidenceCoordinatorInvalidRequest" or "TargetRegistrationEvidenceCoordinatorBusy" or
            "TargetRegistrationEvidenceCoordinatorAlreadyRecorded" or "TargetRegistrationEvidenceCoordinatorUnavailable" or
            "TargetRegistrationEvidencePersistenceUnconfirmed" ? code : "TargetRegistrationEvidenceCoordinatorUnavailable")
    {
        PersistenceMayHaveCompleted = Message == "TargetRegistrationEvidencePersistenceUnconfirmed" && persistenceMayHaveCompleted;
    }
    public string Code => Message;
    public bool PersistenceMayHaveCompleted { get; }
    public override string ToString() => Code;
}

/// <summary>
/// Manual, default-off saved-ACK to paired-observer to append composition; no host wiring or authority.
/// The guard is per instance, NOT a global journal lock: callers must serialize ALL access to the
/// single-owner journal, including other coordinators and historical getters. No durable observation
/// attempt is claimed: pre-append failures may be explicitly observed again with new IDs after a later
/// call/restart, never automatically retried. Existing evidence is never returned as a fresh observation.
/// Expired ACK claim metadata remains history, not renewal. Unknown persistence outcomes require operator
/// inspection through the historical getter, not automatic resume, rollback or release. Cancellation is
/// cooperative; synchronous storage may block/commit past the deadline and is never detached/preempted.
/// </summary>
public sealed class TargetRegistrationEvidenceCoordinator
{
    private readonly AgentJournal journal;
    private readonly TargetRegistrationObserver observer;
    private readonly TimeProvider clock;
    private readonly bool enabled;
    private readonly TimeSpan timeout;
    private readonly Guid agentId, clusterId, sqlId, pairMapping, racMapping, sqlMapping;
    private readonly string sqlServer = "";
    private int busy;

    public TargetRegistrationEvidenceCoordinator(AgentJournal journal, TargetRegistrationObserver observer,
        TargetRegistrationEvidenceCoordinatorOptions options, TimeProvider? clock = null)
    {
        this.journal = journal ?? throw Error("InvalidOptions");
        this.observer = observer ?? throw Error("InvalidOptions");
        if (options is null) throw Error("InvalidOptions");
        this.clock = clock ?? TimeProvider.System;
        enabled = options.Enabled;
        if (!enabled) return;
        if (options.TotalTimeoutSeconds is < 1 or > 120 ||
            new[] { options.ExpectedAgentId, options.ExpectedCanonicalClusterId, options.ExpectedCanonicalSqlServerId,
                options.ExpectedEndpointMappingId, options.RacObservationMappingId, options.SqlObservationMappingId }.Any(id => id == Guid.Empty) ||
            options.ExpectedSqlServer is not { Length: > 0 and <= InfobaseRegistrationReadback.MaxSqlServerCharacters } ||
            options.ExpectedSqlServer[0] == '-' || !options.ExpectedSqlServer.All(c => c is >= '!' and <= '~'))
            throw Error("InvalidOptions");
        timeout = TimeSpan.FromSeconds(options.TotalTimeoutSeconds);
        (agentId, clusterId, sqlId, pairMapping, racMapping, sqlMapping, sqlServer) =
            (options.ExpectedAgentId, options.ExpectedCanonicalClusterId, options.ExpectedCanonicalSqlServerId,
             options.ExpectedEndpointMappingId, options.RacObservationMappingId, options.SqlObservationMappingId, options.ExpectedSqlServer);
    }

    /// <summary>Returns only a newly appended inert observation. Historical evidence causes AlreadyRecorded.
    /// All configuration is trusted; no ownership, fresh authority or physical endpoint identity is proved.</summary>
    public async Task<TargetRegistrationEvidence> ObserveAndRecordAsync(Guid operationId,
        InfobaseRegistrationObserverCredentials credentials, CancellationToken ct = default)
    {
        if (!enabled) throw Error("Disabled");
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw Error("Busy");
        var persistenceEntered = false;
        try
        {
            if (operationId == Guid.Empty || credentials is null ||
                !ValidSecret(credentials.ClusterUser) || !ValidSecret(credentials.ClusterPassword))
                throw Error("InvalidRequest");
            var started = clock.GetTimestamp();
            var previous = started;
            using var timer = new CancellationTokenSource(timeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
            void CheckDeadline()
            {
                var now = clock.GetTimestamp();
                var elapsed = clock.GetElapsedTime(started, now);
                if (now < previous || elapsed < TimeSpan.Zero || elapsed >= timeout || linked.IsCancellationRequested)
                    throw Error("Unavailable");
                previous = now;
            }
            CheckDeadline();
            if (journal.GetTargetRegistrationEvidence(operationId) is not null) throw Error("AlreadyRecorded");
            CheckDeadline();
            var racObservation = Guid.NewGuid();
            Guid sqlObservation;
            do { sqlObservation = Guid.NewGuid(); } while (sqlObservation == racObservation);
            var bridge = TargetCreationRegistrationBridge.Prepare(journal, operationId, agentId, clusterId, sqlId,
                pairMapping, racMapping, sqlMapping, sqlServer, racObservation);
            var expected = new TargetRegistrationAssessmentScope(bridge.Scope, sqlObservation,
                InfobaseRegistrationReadback.ObservedProfileId);
            CheckDeadline();
            var assessment = await observer.ObserveExpectedScopeAsync(expected, credentials, linked.Token).ConfigureAwait(false);
            CheckDeadline();
            if (assessment is null || assessment.Scope != expected || assessment.InfobaseId != bridge.Acknowledgement.ReportedInfobaseId ||
                !string.Equals(assessment.RegistrationProfileId, InfobaseRegistrationReadback.ObservedProfileId, StringComparison.Ordinal) ||
                journal.GetTargetCreationAcknowledgement(operationId) != bridge.Acknowledgement)
                throw Error("Unavailable");
            // Record also revalidates ACK pins and rejects duplicate evidence in its transaction.
            CheckDeadline();
            persistenceEntered = true;
            var evidence = journal.RecordTargetRegistrationEvidence(operationId, assessment);
            CheckDeadline();
            return evidence;
        }
        catch (TargetRegistrationEvidenceCoordinatorException) when (!persistenceEntered) { throw; }
        catch { throw persistenceEntered ? new TargetRegistrationEvidenceCoordinatorException(
            "TargetRegistrationEvidencePersistenceUnconfirmed", true) : Error("Unavailable"); }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    private static bool ValidSecret(string? value) => value is { Length: >= 1 and <= 4096 } && !value.Any(char.IsControl);
    private static TargetRegistrationEvidenceCoordinatorException Error(string suffix) =>
        new("TargetRegistrationEvidenceCoordinator" + suffix, false);
}
