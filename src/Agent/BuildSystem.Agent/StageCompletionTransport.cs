using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public interface IStageCompletionTransport
{
    Task<StageCompletionReceipt> CompleteStageAsync(CompleteStage report, CancellationToken ct);
}

public sealed partial class AgentTransport : IStageCompletionTransport
{
    public async Task<StageCompletionReceipt> CompleteStageAsync(CompleteStage report, CancellationToken ct)
    {
        if (report is null || report.JobId == Guid.Empty || report.FencingToken <= 0 || report.ArtifactId == Guid.Empty)
            throw new ArgumentException("Invalid completion identity.", nameof(report));
        var receipt = await StageRequestAsync<StageCompletionReceipt>("api/agent/stage/complete", report, false, ct)
            ?? throw new InvalidOperationException("Invalid completion receipt.");
        if (receipt.JobId != report.JobId || receipt.FencingToken != report.FencingToken ||
            receipt.ArtifactId != report.ArtifactId || receipt.State != "StageCompleted")
            throw new InvalidOperationException("Invalid completion receipt.");
        return receipt;
    }
}
// end
