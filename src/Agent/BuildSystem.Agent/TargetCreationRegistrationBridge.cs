using BuildSystem.Agent.Execution;

namespace BuildSystem.Agent;

/// <summary>Historical correlation and a readback request, not verified registration or authority.</summary>
public sealed record TargetCreationRegistrationBridgeReceipt(InfobaseRegistrationReadbackScope Scope,
    TargetCreationAcknowledgement Acknowledgement)
{
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool OwnershipEstablished => false;
    public bool RestoreAllowed => false;
    public bool RetryAllowed => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool RequiresIndependentVerification => true;
    public override string ToString() => "TargetCreationRegistrationBridgeReceipt [independent verification required; details omitted]";
}

public sealed class TargetCreationRegistrationBridgeUnavailableException : Exception
{
    public TargetCreationRegistrationBridgeUnavailableException() : base("TargetCreationRegistrationBridgeUnavailable") { }
    public string Code => Message;
    public override string ToString() => "TargetCreationRegistrationBridgeUnavailable";
}

/// <summary>
/// Reads the actual saved ACK through the journal's strict state/revision/pin validation. Public
/// acknowledgement records alone cannot prepare a request. All expected IDs and the literal SQL
/// server must come from trusted configuration; observation mappings are independent submappings,
/// not necessarily the credential pair mapping. No aliases are resolved or physical identities proved.
/// ObservationId is caller correlation only, with no freshness or replay guarantee. Original claim
/// times and fencing metadata remain historical: no lease, budget or execution authority is renewed.
/// No writes, namespace locks, credentials, CLI, RAC/SQL commands or host wiring are performed.
/// Independent verification, including a real version-specific RAC fixture/adapter, remains pending.
/// </summary>
public static class TargetCreationRegistrationBridge
{
    public static TargetCreationRegistrationBridgeReceipt Prepare(AgentJournal journal, Guid operationId,
        Guid expectedAgentId, Guid expectedCanonicalClusterId, Guid expectedCanonicalSqlServerId,
        Guid expectedEndpointMappingId, Guid racObservationMappingId, Guid sqlObservationMappingId,
        string expectedSqlServer, Guid observationId)
    {
        try
        {
            if (journal is null || new[] { operationId, expectedAgentId, expectedCanonicalClusterId,
                    expectedCanonicalSqlServerId, expectedEndpointMappingId, racObservationMappingId,
                    sqlObservationMappingId, observationId }.Any(id => id == Guid.Empty) ||
                expectedSqlServer is not { Length: > 0 and <= InfobaseRegistrationReadback.MaxSqlServerCharacters } ||
                expectedSqlServer[0] == '-' || !expectedSqlServer.All(c => c is >= '!' and <= '~'))
                throw new TargetCreationRegistrationBridgeUnavailableException();

            var acknowledgement = journal.GetTargetCreationAcknowledgement(operationId)
                ?? throw new TargetCreationRegistrationBridgeUnavailableException();
            var intent = acknowledgement.Intent;
            var binding = acknowledgement.Binding;
            if (binding.AgentId != expectedAgentId || intent.CanonicalClusterId != expectedCanonicalClusterId ||
                intent.CanonicalSqlServerId != expectedCanonicalSqlServerId || binding.EndpointMappingId != expectedEndpointMappingId)
                throw new TargetCreationRegistrationBridgeUnavailableException();

            return new(new(intent.OperationId, intent.FencingToken, intent.TargetIncarnationId,
                intent.ReservationId, intent.CanonicalClusterId, intent.CanonicalSqlServerId,
                racObservationMappingId, sqlObservationMappingId, binding.AttemptId,
                acknowledgement.ClaimId, acknowledgement.ReportedInfobaseId, observationId,
                intent.InfobaseName, expectedSqlServer, intent.SqlDatabaseName), acknowledgement);
        }
        catch (Exception)
        {
            // Never retain persisted text, filesystem paths, configuration or an inner exception.
            throw new TargetCreationRegistrationBridgeUnavailableException();
        }
    }
}
