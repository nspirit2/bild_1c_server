using System.Security.Cryptography;
using System.Text;
using BuildSystem.Agent.Archives;

namespace BuildSystem.Agent;

/// <summary>Explicit local execution only; no worker, HTTP dispatch, result delivery or quota release.
/// The caller must serialize journal access and all coordinator invocations, including stage work.</summary>
public sealed class StageCleanupCoordinator(AgentJournal journal, ArchiveStaging staging)
{
    public async Task ExecuteAsync(Guid authorizationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var grant = journal.BeginCleanupExecution(authorizationId);
        var p = grant.Plan;
        var intent = journal.GetStageIntent(p.JobId, p.FencingToken)
            ?? throw new InvalidOperationException("Cleanup stage intent unavailable.");
        var sourceNameHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(intent.RelativePath))).ToLowerInvariant();
        var format = p.Format switch { "dt" => ArchiveFormat.Dt, "bak" => ArchiveFormat.Bak, _ => throw new InvalidOperationException("Invalid cleanup format.") };
        // Even DeletedLocal replays call the engine: its completed marker must verify absence,
        // rather than treating a recreated artifact directory as already safely deleted.
        await staging.CleanupAsync(new ArchiveCleanupRequest(grant.AuthorizationId, p.ArtifactId,
            p.ReservationId, p.ArchiveVersionId, intent.RootId, sourceNameHash, p.Sha256, p.SizeBytes, format), ct);
        journal.MarkCleanupDeletedLocal(authorizationId);
    }
}
// end
