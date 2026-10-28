using System.Text.Json;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public sealed partial class AgentJournal
{
    private void InitializeCleanupAuthorizations() => Execute("""
        CREATE TABLE IF NOT EXISTS cleanup_authorizations(
            id TEXT PRIMARY KEY,retirement TEXT NOT NULL UNIQUE,artifact TEXT NOT NULL UNIQUE,
            payload TEXT NOT NULL,state TEXT NOT NULL CHECK(state='Prepared'));
        """);

    /// <summary>Persists prepared authority only. Does not execute cleanup or prove current remote authorization.
    /// Every store, including replay, requires a settled and acknowledged local stage plus globally quiet outboxes.</summary>
    public void StoreCleanupAuthorization(StageCleanupAuthorization grant)
    {
        if (grant is null || grant.AuthorizationId == Guid.Empty || grant.AuthorizedAt == default)
            throw new ArgumentException("Invalid cleanup authorization.", nameof(grant));
        RetirementPlanValidation.Validate(grant.Plan);
        grant = grant with { AuthorizedAt = grant.AuthorizedAt.ToUniversalTime() };
        var payload = JsonSerializer.Serialize(grant);
        var plan = grant.Plan;
        using var transaction = _db.BeginTransaction();
        using (var lease = Command("SELECT kind,settled FROM leases WHERE job=$0 AND fence=$1", plan.JobId.ToString(), plan.FencingToken))
        using (var reader = lease.ExecuteReader())
            if (!reader.Read() || reader.GetString(0) != "ArchiveStageOnly" || reader.GetInt64(1) != 1)
                throw new InvalidOperationException("Cleanup preparation requires a settled stage-only lease.");
        var intent = GetStageIntent(plan.JobId, plan.FencingToken);
        if (intent is null || StageIntentFailure(plan.JobId, plan.FencingToken) is not null ||
            intent.ReservationId != plan.ReservationId || intent.ArchiveVersionId != plan.ArchiveVersionId ||
            intent.Sha256 != plan.Sha256 || intent.SizeBytes != plan.SizeBytes || intent.Format != plan.Format)
            throw new InvalidOperationException("Cleanup authorization conflicts with stage intent.");
        var artifact = AckedArtifactForLease(plan.JobId, plan.FencingToken);
        if (artifact is null || artifact.ArtifactId != plan.ArtifactId || artifact.ReservationId != plan.ReservationId ||
            artifact.ArchiveVersionId != plan.ArchiveVersionId || artifact.Sha256 != plan.Sha256 ||
            artifact.SizeBytes != plan.SizeBytes || artifact.Format != plan.Format)
            throw new InvalidOperationException("Cleanup preparation requires exact acknowledged artifact.");
        using (var completion = Command("SELECT COUNT(*) FROM stage_completions WHERE job=$0 AND fence=$1 AND artifact=$2 AND state='Acked'",
            plan.JobId.ToString(), plan.FencingToken, plan.ArtifactId.ToString()))
            if ((long)completion.ExecuteScalar()! != 1)
                throw new InvalidOperationException("Cleanup preparation requires exact acknowledged completion.");
        using (var quiet = Command("""
            SELECT (SELECT COUNT(*) FROM leases WHERE settled<>1) +
                (SELECT COUNT(*) FROM events WHERE acked<>1) +
                (SELECT COUNT(*) FROM artifacts WHERE state<>'Acked') +
                (SELECT COUNT(*) FROM stage_completions WHERE state<>'Acked')
            """))
            if ((long)quiet.ExecuteScalar()! != 0)
                throw new InvalidOperationException("Unsettled journal state blocks cleanup preparation.");
        using (var prior = Command("SELECT payload FROM cleanup_authorizations WHERE id=$0", grant.AuthorizationId.ToString()))
        {
            if (prior.ExecuteScalar() is string existing)
            {
                if (existing != payload) throw new InvalidOperationException("Cleanup authorization identity conflict.");
                transaction.Commit(); return;
            }
        }
        using (var binding = Command("SELECT COUNT(*) FROM cleanup_authorizations WHERE retirement=$0 OR artifact=$1",
            plan.RetirementId.ToString(), plan.ArtifactId.ToString()))
            if ((long)binding.ExecuteScalar()! != 0)
                throw new InvalidOperationException("Cleanup artifact or retirement already bound to another authorization.");
        Execute("INSERT INTO cleanup_authorizations(id,retirement,artifact,payload,state) VALUES($0,$1,$2,$3,'Prepared')",
            grant.AuthorizationId.ToString(), plan.RetirementId.ToString(), plan.ArtifactId.ToString(), payload);
        transaction.Commit();
    }

    public StageCleanupAuthorization? GetCleanupAuthorization(Guid authorizationId)
    {
        using var command = Command("SELECT payload FROM cleanup_authorizations WHERE id=$0", authorizationId.ToString());
        return command.ExecuteScalar() is string payload ? JsonSerializer.Deserialize<StageCleanupAuthorization>(payload) : null;
    }

    /// <summary>Prepared records for reconciliation only; this list is not an executable cleanup queue.</summary>
    public IReadOnlyList<StageCleanupAuthorization> PendingCleanupAuthorizations()
    {
        using var command = Command("SELECT payload FROM cleanup_authorizations WHERE state='Prepared' ORDER BY rowid");
        using var reader = command.ExecuteReader(); var result = new List<StageCleanupAuthorization>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<StageCleanupAuthorization>(reader.GetString(0)) ??
            throw new InvalidOperationException("Invalid persisted cleanup authorization."));
        return result;
    }
}
// end
