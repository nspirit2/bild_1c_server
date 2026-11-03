namespace BuildSystem.Application;

/// <summary>Operator assertion of canonical identity, not discovery or proof; aliases are unsupported.</summary>
public sealed record RestoreIntentCanonicalMapping(Guid CanonicalClusterId, Guid CanonicalSqlServerId)
{
    public override string ToString() => "RestoreIntentCanonicalMapping [redacted]";
}
public sealed class RestoreIntentOptions
{
    public bool Enabled { get; init; }
    public Dictionary<Guid, RestoreIntentCanonicalMapping> CanonicalMappings { get; init; } = [];
    public override string ToString() => "RestoreIntentOptions [redacted]";
}

/// <summary>Only exact retained-stage pins are caller-controlled. Grants no restore authority.</summary>
public sealed record RequestRestoreIntent(Guid ExpectedStageJobId, Guid ExpectedArtifactId);
public sealed record RestoreIntentReceipt(Guid RestoreIntentId, Guid EnvironmentId, Guid StageJobId,
    Guid ReservationId, Guid ArtifactId, Guid TargetIncarnation, Guid CanonicalClusterId,
    Guid CanonicalSqlServerId, string InfobaseName, string SqlDbName, DateTimeOffset RequestedAt)
{
    public bool Executable => false;
    public override string ToString() => "RestoreIntentReceipt [NONEXECUTABLE]";
}
public interface IRestoreIntents
{
    Task<RestoreIntentReceipt> RequestAsync(Actor actor, Guid environmentId, RequestRestoreIntent input, CancellationToken ct);
}
// end
