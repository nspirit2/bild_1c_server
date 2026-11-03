using BuildSystem.Contracts;

namespace BuildSystem.Application;

/// <summary>Operator-configured endpoint identities, not discovered aliases or proof of physical identity.</summary>
public sealed record TargetObservationEndpointMapping(Guid RacEndpointMappingId, Guid SqlEndpointMappingId)
{
    public override string ToString() => "TargetObservationEndpointMapping [redacted]";
}

public sealed class TargetObservationOptions
{
    public bool Enabled { get; init; }
    public Dictionary<Guid, TargetObservationEndpointMapping> EndpointMappings { get; init; } = [];
    public override string ToString() => "TargetObservationOptions [redacted]";
}

/// <summary>Assigns immutable observation correlation only; grants no execution, lease or mutation authority.</summary>
public interface ITargetObservations
{
    Task<TargetObservationAssignment> RequestAsync(Actor actor, Guid environmentId,
        Guid expectedRestoreIntentId, CancellationToken ct);
}
// end
