using BuildSystem.Contracts;

namespace BuildSystem.Application;

/// <summary>Accepts historical observation evidence only; grants no execution, ownership or freshness authority.</summary>
public interface ITargetObservationReceipts
{
    Task<TargetObservationReceipt> ReportAsync(string credential, ReportTargetObservation report, CancellationToken ct);
}
// end
