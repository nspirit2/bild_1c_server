using BuildSystem.Agent.Tools;

namespace BuildSystem.Agent;

/// <summary>Derived correlation pins only; no fresh authority, held namespace, or physical discovery.</summary>
public sealed record TargetCreationScopeBridgeReceipt(RacInfobaseCreationScope CreationScope,
    SqlTargetNamespaceScope NamespaceScope, TargetPresenceRequest PresenceRequest,
    TargetCreationRecord Record, TargetCreationPermitBinding Binding)
{
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool RetryAllowed => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetCreationScopeBridgeReceipt [NONEXECUTABLE]";
}

public sealed class TargetCreationScopeBridgeException : Exception
{
    public TargetCreationScopeBridgeException() : base("InvalidTargetCreationScopeBridge") { }
    public string Code => Message;
    public override string ToString() => "InvalidTargetCreationScopeBridge";
}

/// <summary>
/// Pure validation and projection of the exact sole in-flight local attempt, not journal evidence.
/// All expected IDs must originate in trusted configuration. The creation mapping identifies the
/// whole trusted RAC/SQL pair and must also match the SQL namespace guard's configured mapping.
/// RAC and SQL observation mappings are independently configured submappings, not the credential
/// pair mapping; this helper does not discover or certify their physical relationship to that pair.
/// The copied fencing token is correlation metadata, NEVER an active fence or freshness proof.
/// No credential preparation, recipe construction, IO, worker registration, or execution occurs.
/// </summary>
public static class TargetCreationScopeBridge
{
    public static TargetCreationScopeBridgeReceipt Prepare(TargetCreationRecord record,
        TargetCreationPermitBinding binding, Guid expectedAgentId, Guid expectedCanonicalClusterId,
        Guid expectedCanonicalSqlServerId, Guid expectedEndpointMappingId,
        Guid racObservationMappingId, Guid sqlObservationMappingId)
    {
        try { TargetCreationLocalPreflight.ValidateInFlight(record, binding); }
        catch (InvalidOperationException) { throw new TargetCreationScopeBridgeException(); }

        var intent = record.Intent;
        if (new[] { expectedAgentId, expectedCanonicalClusterId, expectedCanonicalSqlServerId,
                expectedEndpointMappingId, racObservationMappingId, sqlObservationMappingId }.Any(id => id == Guid.Empty) ||
            binding.AgentId != expectedAgentId || intent.CanonicalClusterId != expectedCanonicalClusterId ||
            intent.CanonicalSqlServerId != expectedCanonicalSqlServerId || binding.EndpointMappingId != expectedEndpointMappingId)
            throw new TargetCreationScopeBridgeException();

        return new(new(intent.OperationId, binding.PermitId, binding.AttemptId, intent.ReservationId,
                intent.TargetIncarnationId, intent.CanonicalClusterId, intent.CanonicalSqlServerId, expectedEndpointMappingId),
            new(intent.OperationId, binding.AttemptId, intent.ReservationId, intent.CanonicalSqlServerId, expectedEndpointMappingId),
            new(intent.OperationId, intent.FencingToken, intent.TargetIncarnationId, intent.ReservationId,
                intent.CanonicalClusterId, intent.CanonicalSqlServerId, racObservationMappingId, sqlObservationMappingId),
            record, binding);
    }
}
