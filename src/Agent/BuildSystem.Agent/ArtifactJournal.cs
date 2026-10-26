using System.Globalization;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public sealed record ArtifactOutboxItem(ReportStagedArtifact Report, DateTimeOffset ExpiresAt,
    string State, string? FailureCode = null);

public sealed partial class AgentJournal
{
    private void InitializeArtifacts()
    {
        // Pre-upgrade leases have no trusted Kind. Never infer it from a replayed grant.
        using (var columns = Command("PRAGMA table_info(leases)"))
        using (var reader = columns.ExecuteReader())
        {
            var hasKind = false;
            while (reader.Read()) hasKind |= reader.GetString(1) == "kind";
            reader.Close();
            if (!hasKind) Execute("ALTER TABLE leases ADD COLUMN kind TEXT NOT NULL DEFAULT 'Unknown'");
        }
        Execute("""
            CREATE TABLE IF NOT EXISTS artifacts(
                id TEXT PRIMARY KEY,job TEXT NOT NULL,fence INTEGER NOT NULL,
                reservation TEXT NOT NULL,version TEXT NOT NULL,sha TEXT NOT NULL,
                size INTEGER NOT NULL,format TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'Pending',failure TEXT,
                UNIQUE(job,fence),UNIQUE(reservation,version),
                FOREIGN KEY(job,fence) REFERENCES leases(job,fence));
            """);
    }

    private static void ValidateArtifact(ReportStagedArtifact report)
    {
        if (report is null || report.JobId == Guid.Empty || report.FencingToken <= 0 ||
            report.ReservationId == Guid.Empty || report.ArchiveVersionId == Guid.Empty ||
            report.ArtifactId == Guid.Empty || report.SizeBytes <= 0 || report.Format is not ("dt" or "bak") ||
            report.Sha256 is null || report.Sha256.Length != 64 ||
            report.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid staged artifact metadata.", nameof(report));
    }

    public void EnqueueArtifact(ReportStagedArtifact report)
    {
        ValidateArtifact(report);
        report = report with { Sha256 = report.Sha256.ToUpperInvariant() };
        using var transaction = _db.BeginTransaction();
        using (var existing = Command("SELECT job,fence,reservation,version,sha,size,format FROM artifacts WHERE id=$0", report.ArtifactId.ToString()))
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != report.JobId.ToString() || reader.GetInt64(1) != report.FencingToken ||
                    reader.GetString(2) != report.ReservationId.ToString() ||
                    reader.GetString(3) != report.ArchiveVersionId.ToString() ||
                    reader.GetString(4) != report.Sha256 || reader.GetInt64(5) != report.SizeBytes ||
                    reader.GetString(6) != report.Format)
                    throw new InvalidOperationException("Artifact identity payload conflict.");
                reader.Close(); transaction.Commit(); return;
            }
        }
        ValidateStageArtifact(report);
        using (var lease = Command("SELECT expiry,kind,settled FROM leases WHERE job=$0 AND fence=$1", report.JobId.ToString(), report.FencingToken))
        using (var reader = lease.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(1) != "ProvisionArchive" || reader.GetInt64(2) != 0 ||
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture) <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Artifact requires an unexpired unsettled ProvisionArchive lease.");
        }
        // Unique constraints retain identity and slot bindings even after acknowledgement.
        Execute("INSERT INTO artifacts(id,job,fence,reservation,version,sha,size,format) VALUES($0,$1,$2,$3,$4,$5,$6,$7)",
            report.ArtifactId.ToString(), report.JobId.ToString(), report.FencingToken,
            report.ReservationId.ToString(), report.ArchiveVersionId.ToString(),
            report.Sha256, report.SizeBytes, report.Format);
        transaction.Commit();
    }

    public IReadOnlyList<ArtifactOutboxItem> PendingArtifacts()
    {
        using var transaction = _db.BeginTransaction();
        Execute("""
            UPDATE artifacts SET state='Blocked',failure='LeaseRejected'
            WHERE state='Pending' AND EXISTS
            (SELECT 1 FROM leases WHERE job=artifacts.job AND fence=artifacts.fence AND settled=1)
            """);
        Execute("""
            UPDATE artifacts SET state='Blocked',failure='LeaseExpired'
            WHERE state='Pending' AND EXISTS
            (SELECT 1 FROM leases WHERE job=artifacts.job AND fence=artifacts.fence AND expiry<=$0)
            """, Stamp(DateTimeOffset.UtcNow));
        using var command = Command("""
            SELECT a.id,a.job,a.fence,a.reservation,a.version,a.sha,a.size,a.format,l.expiry,a.state,a.failure
            FROM artifacts a JOIN leases l ON l.job=a.job AND l.fence=a.fence
            WHERE a.state IN ('Pending','Blocked') ORDER BY a.rowid
            """);
        using var reader = command.ExecuteReader();
        var result = new List<ArtifactOutboxItem>();
        while (reader.Read())
        {
            var report = new ReportStagedArtifact(Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                Guid.Parse(reader.GetString(3)), Guid.Parse(reader.GetString(4)), Guid.Parse(reader.GetString(0)),
                reader.GetString(5), reader.GetInt64(6), reader.GetString(7));
            result.Add(new(report, DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        reader.Close(); transaction.Commit(); return result;
    }

    public string? ArtifactState(Guid artifactId)
    {
        using var command = Command("SELECT state FROM artifacts WHERE id=$0", artifactId.ToString());
        return command.ExecuteScalar() as string;
    }

    public void AcknowledgeArtifact(Guid artifactId)
    {
        using var transaction = _db.BeginTransaction();
        using var read = Command("SELECT state FROM artifacts WHERE id=$0", artifactId.ToString());
        var state = read.ExecuteScalar() as string;
        if (state is null) throw new InvalidOperationException("Unknown artifact acknowledgement.");
        if (state == "Blocked") throw new InvalidOperationException("Blocked artifact cannot be acknowledged.");
        Execute("UPDATE artifacts SET state='Acked' WHERE id=$0", artifactId.ToString());
        transaction.Commit();
    }

    public void BlockArtifact(Guid artifactId, string code)
    {
        if (code is not ("LeaseExpired" or "LeaseRejected" or "ReceiptMismatch" or "AuthRejected"))
            throw new ArgumentException("Invalid artifact failure code.", nameof(code));
        using var transaction = _db.BeginTransaction();
        using var read = Command("SELECT state,failure FROM artifacts WHERE id=$0", artifactId.ToString());
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Unknown artifact.");
            if (reader.GetString(0) == "Acked") throw new InvalidOperationException("Acknowledged artifact cannot be blocked.");
            if (reader.GetString(0) == "Blocked" && reader.GetString(1) != code)
                throw new InvalidOperationException("Artifact already blocked with a different reason.");
        }
        Execute("UPDATE artifacts SET state='Blocked',failure=$1 WHERE id=$0", artifactId.ToString(), code);
        transaction.Commit();
    }
}
