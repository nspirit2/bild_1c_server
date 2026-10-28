using BuildSystem.Contracts;

namespace BuildSystem.Application;

public sealed class StageCleanupOptions
{
    public bool Enabled { get; init; }
}

public interface IStageCleanup
{
    Task<StageCleanupAuthorization> ClaimAsync(string credential, ClaimStageCleanup request, CancellationToken ct);
}
// end
