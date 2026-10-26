using System.Globalization;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public sealed record StageCompletionOutboxItem(CompleteStage Report, string State, string? FailureCode);

public sealed partial class AgentJournal
{
    private void InitializeStageCompletions() => Execute("""
        CREATE TABLE IF NOT EXISTS stage_completions(
            job TEXT NOT NULL,fence INTEGER NOT NULL,artifact TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL CHECK(state IN ('Pending','Blocked','Acked')),failure TEXT,
            PRIMARY KEY(job,fence),FOREIGN KEY(job,fence) REFERENCES leases(job,fence),
            FOREIGN KEY(artifact) REFERENCES artifacts(id));
        """);

    public ReportStagedArtifact? AckedArtifactForLease(Guid jobId, long fence)
    {
        using var command = Command("SELECT id,reservation,version,sha,size,format FROM artifacts WHERE job=$0 AND fence=$1 AND state='Acked'", jobId.ToString(), fence);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(jobId, fence, Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(0)), reader.GetString(3), reader.GetInt64(4), reader.GetString(5)) : null;
    }

    public void EnqueueCompletion(CompleteStage report)
    {
        if (report is null || report.JobId == Guid.Empty || report.FencingToken <= 0 || report.ArtifactId == Guid.Empty)
            throw new ArgumentException("Invalid completion identity.", nameof(report));
        using var transaction = _db.BeginTransaction();
        using (var command = Command("SELECT artifact FROM stage_completions WHERE job=$0 AND fence=$1", report.JobId.ToString(), report.FencingToken))
        {
            if (command.ExecuteScalar() is string artifact)
            {
                if (artifact != report.ArtifactId.ToString()) throw new InvalidOperationException("Completion identity conflict.");
                transaction.Commit(); return; // Preserve Pending across expiry, and never reset Blocked/Acked.
            }
        }
        var acknowledged = AckedArtifactForLease(report.JobId, report.FencingToken);
        if (acknowledged?.ArtifactId != report.ArtifactId || StageIntentFailure(report.JobId, report.FencingToken) is not null)
            throw new InvalidOperationException("Completion requires a matching acknowledged artifact and available stage intent.");
        DateTimeOffset expiry;
        using (var command = Command("SELECT kind,settled,expiry FROM leases WHERE job=$0 AND fence=$1", report.JobId.ToString(), report.FencingToken))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "ArchiveStageOnly" || reader.GetInt64(1) != 0)
                throw new InvalidOperationException("Completion requires an unsettled stage-only lease.");
            expiry = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
        }
        var expired = expiry <= DateTimeOffset.UtcNow;
        Execute("INSERT INTO stage_completions(job,fence,artifact,state,failure) VALUES($0,$1,$2,$3,$4)",
            report.JobId.ToString(), report.FencingToken, report.ArtifactId.ToString(), expired ? "Blocked" : "Pending",
            expired ? "LeaseExpired" : (object)DBNull.Value);
        transaction.Commit();
    }

    public IReadOnlyList<StageCompletionOutboxItem> PendingCompletions()
    {
        using var command = Command("SELECT job,fence,artifact,state,failure FROM stage_completions WHERE state IN ('Pending','Blocked') ORDER BY rowid");
        using var reader = command.ExecuteReader();
        var result = new List<StageCompletionOutboxItem>();
        while (reader.Read()) result.Add(new(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), Guid.Parse(reader.GetString(2))),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    public string? CompletionState(Guid jobId, long fence)
    {
        using var command = Command("SELECT state FROM stage_completions WHERE job=$0 AND fence=$1", jobId.ToString(), fence);
        return command.ExecuteScalar() as string;
    }

    public void BlockCompletion(Guid jobId, long fence, string code)
    {
        if (code is not ("LeaseExpired" or "LeaseRejected" or "AuthRejected" or "ReceiptMismatch"))
            throw new ArgumentException("Invalid completion failure code.", nameof(code));
        using var transaction = _db.BeginTransaction();
        using (var command = Command("SELECT state,failure FROM stage_completions WHERE job=$0 AND fence=$1", jobId.ToString(), fence))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) == "Acked") throw new InvalidOperationException("No blockable completion.");
            if (reader.GetString(0) == "Blocked")
            {
                if (reader.GetString(1) != code) throw new InvalidOperationException("Completion already blocked with another reason.");
                reader.Close(); transaction.Commit(); return;
            }
        }
        Execute("UPDATE stage_completions SET state='Blocked',failure=$2 WHERE job=$0 AND fence=$1", jobId.ToString(), fence, code);
        transaction.Commit();
    }

    public void AcknowledgeCompletion(StageCompletionReceipt receipt)
    {
        if (receipt is null || receipt.State != "StageCompleted") throw new InvalidOperationException("Invalid completion receipt.");
        using var transaction = _db.BeginTransaction();
        using (var command = Command("SELECT artifact,state FROM stage_completions WHERE job=$0 AND fence=$1", receipt.JobId.ToString(), receipt.FencingToken))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != receipt.ArtifactId.ToString() || reader.GetString(1) == "Blocked")
                throw new InvalidOperationException("Completion receipt mismatch.");
        }
        if (AckedArtifactForLease(receipt.JobId, receipt.FencingToken)?.ArtifactId != receipt.ArtifactId)
            throw new InvalidOperationException("Artifact receipt is not acknowledged.");
        Execute("UPDATE stage_completions SET state='Acked',failure=NULL WHERE job=$0 AND fence=$1", receipt.JobId.ToString(), receipt.FencingToken);
        Execute("UPDATE leases SET settled=1 WHERE job=$0 AND fence=$1 AND kind='ArchiveStageOnly'", receipt.JobId.ToString(), receipt.FencingToken);
        transaction.Commit();
    }
}
// end
