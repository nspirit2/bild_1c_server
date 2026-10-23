using BuildSystem.Contracts;

namespace BuildSystem.Application;

public interface IArtifactReceipts
{
    Task<StagedArtifactReceipt> ReportAsync(string credential, ReportStagedArtifact report, CancellationToken ct);
}
