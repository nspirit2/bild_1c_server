using BuildSystem.Contracts;

namespace BuildSystem.Application;

public sealed class StageCleanupResultOptions { public bool Enabled { get; set; } }
public interface IStageCleanupResults
{
    Task<StageCleanupResultReceipt> ReportAsync(string credential, ReportStageCleanup input, CancellationToken ct);
}
// end
