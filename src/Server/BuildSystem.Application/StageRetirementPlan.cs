using BuildSystem.Contracts;

namespace BuildSystem.Application;

/// <summary>Read-only opt-in machine observation; not cleanup, lease, dispatch or acknowledgment.</summary>
public interface IStageRetirementPlans
{
    Task<StageRetirementPlan?> PollAsync(string credential, CancellationToken ct);
}

/// <summary>Default false. The caller must explicitly opt in; this does not enable deletion.</summary>
public sealed class StageRetirementOptions
{
    public bool Enabled { get; init; }
}
// end
