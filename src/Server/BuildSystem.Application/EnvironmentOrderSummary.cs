namespace BuildSystem.Application;

public interface IEnvironmentOrderSummary
{
    Task<EnvironmentOrderSummaryView?> GetAsync(Actor actor, Guid environmentId, CancellationToken ct);
}

public sealed record EnvironmentOrderSummaryView(
    Guid EnvironmentId, Guid ProjectId, string DisplayName, string EnvironmentState,
    Guid RequestId, string RequestState, DateTimeOffset CreatedAt, int TtlDays,
    Guid JobId, string JobKind, string JobState, EnvironmentOrderSourceView Source);

public sealed record EnvironmentOrderSourceView(Guid ArchiveVersionId, string Format, string Sha256, long SizeBytes);
