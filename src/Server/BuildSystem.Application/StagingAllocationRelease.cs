using BuildSystem.Contracts;

namespace BuildSystem.Application;

public sealed class StagingAllocationReleaseOptions { public bool Enabled { get; set; } }
public interface IStagingAllocationRelease
{
    Task<StagingAllocationReleaseReceipt> ReleaseAsync(Actor actor, Guid resultId, ReleaseStagingAllocation input, CancellationToken ct);
}
// end
