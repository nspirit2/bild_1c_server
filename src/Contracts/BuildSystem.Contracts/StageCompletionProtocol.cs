namespace BuildSystem.Contracts;

/// <summary>Definitive stage-only completion; never authorizes restoration or environment readiness.</summary>
public sealed record CompleteStage(Guid JobId, long FencingToken, Guid ArtifactId);
public sealed record StageCompletionReceipt(Guid JobId, long FencingToken, Guid ArtifactId, string State);
// end
