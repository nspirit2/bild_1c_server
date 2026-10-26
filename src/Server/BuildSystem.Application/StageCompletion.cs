using BuildSystem.Contracts;

namespace BuildSystem.Application;

public interface IStageCompletion
{
    Task<StageCompletionReceipt> CompleteAsync(string credential, CompleteStage request, CancellationToken ct);
}
// end
