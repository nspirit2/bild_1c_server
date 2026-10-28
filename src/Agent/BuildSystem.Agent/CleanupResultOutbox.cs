using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public sealed partial class AgentJournal
{
    private void InitializeCleanupResults() => Execute("""
        CREATE TABLE IF NOT EXISTS cleanup_results(
            result TEXT PRIMARY KEY, authorization TEXT NOT NULL UNIQUE REFERENCES cleanup_authorizations(id),
            retirement TEXT NOT NULL,job TEXT NOT NULL,artifact TEXT NOT NULL,fence INTEGER NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('Pending','Acked','Blocked')),received TEXT);
        """);

    // Caller owns the same transaction that records DeletedLocal. No public enqueue API: a durable
    // filesystem completion verified by the coordinator is the sole path to creating this record.
    private void StoreCleanupResult(StageCleanupAuthorization grant)
    {
        var p = grant.Plan;
        using (var command = Command("SELECT result,retirement,job,artifact,fence FROM cleanup_results WHERE authorization=$0", grant.AuthorizationId.ToString()))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(1) != p.RetirementId.ToString() || reader.GetString(2) != p.JobId.ToString() ||
                    reader.GetString(3) != p.ArtifactId.ToString() || reader.GetInt64(4) != p.FencingToken)
                    throw new InvalidOperationException("Cleanup result identity conflict.");
                return; // Preserve ResultId, Acked/Blocked and immutable receipt on local replay.
            }
        }
        Execute("INSERT INTO cleanup_results(result,authorization,retirement,job,artifact,fence,state) VALUES($0,$1,$2,$3,$4,$5,'Pending')",
            Guid.NewGuid().ToString(), grant.AuthorizationId.ToString(), p.RetirementId.ToString(), p.JobId.ToString(), p.ArtifactId.ToString(), p.FencingToken);
    }

    /// <summary>Allowlisted durable results awaiting explicit delivery. Does not perform network IO.</summary>
    public IReadOnlyList<ReportStageCleanup> PendingCleanupResults()
    {
        using var command = Command("SELECT result,authorization,retirement,job,artifact,fence FROM cleanup_results WHERE state='Pending' ORDER BY rowid");
        using var reader = command.ExecuteReader(); var result = new List<ReportStageCleanup>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), Guid.Parse(reader.GetString(4)), reader.GetInt64(5)));
        return result;
    }

    public string? GetCleanupResultState(Guid resultId)
    {
        using var command = Command("SELECT state FROM cleanup_results WHERE result=$0", resultId.ToString());
        return command.ExecuteScalar() as string;
    }

    public void AckCleanupResult(StageCleanupResultReceipt receipt)
    {
        if (receipt is null || receipt.ResultId == Guid.Empty || receipt.AuthorizationId == Guid.Empty ||
            receipt.ArtifactId == Guid.Empty || receipt.State != "DeletedReported" || receipt.ReceivedAt == default)
            throw new InvalidOperationException("Invalid cleanup result receipt.");
        var received = Stamp(receipt.ReceivedAt);
        using var transaction = _db.BeginTransaction();
        using (var command = Command("SELECT authorization,artifact,state,received FROM cleanup_results WHERE result=$0", receipt.ResultId.ToString()))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != receipt.AuthorizationId.ToString() ||
                reader.GetString(1) != receipt.ArtifactId.ToString() || reader.GetString(2) == "Blocked")
                throw new InvalidOperationException("Cleanup result receipt mismatch.");
            if (reader.GetString(2) == "Acked")
            {
                if (reader.IsDBNull(3) || reader.GetString(3) != received)
                    throw new InvalidOperationException("Cleanup result receipt changed.");
                reader.Close(); transaction.Commit(); return;
            }
        }
        Execute("UPDATE cleanup_results SET state='Acked',received=$1 WHERE result=$0", receipt.ResultId.ToString(), received);
        transaction.Commit();
    }

    public void BlockCleanupResult(Guid resultId)
    {
        using var transaction = _db.BeginTransaction();
        using (var command = Command("SELECT state FROM cleanup_results WHERE result=$0", resultId.ToString()))
        {
            var state = command.ExecuteScalar() as string;
            if (state is not ("Pending" or "Blocked")) throw new InvalidOperationException("No blockable cleanup result.");
        }
        Execute("UPDATE cleanup_results SET state='Blocked' WHERE result=$0", resultId.ToString());
        transaction.Commit();
    }
}
// end
