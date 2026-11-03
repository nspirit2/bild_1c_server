using BuildSystem.Contracts;

namespace BuildSystem.Application;

/// <summary>Reads one addressed historical assignment; grants no execution or freshness authority.</summary>
public interface ITargetObservationDelivery
{
    Task<TargetObservationAssignment> ReadAsync(string credential, Guid observationRequestId, CancellationToken ct);
}
// end
