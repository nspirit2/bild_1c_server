using System.Globalization;

namespace BuildSystem.Agent;

/// <summary>Sensitive local recovery metadata. Resolve only against approved configured roots; never log the path.</summary>
public sealed record StageIntent(Guid JobId, long FencingToken, Guid ReservationId, Guid ArchiveVersionId,
    Guid RootId, string RelativePath, string Sha256, long SizeBytes, string Format)
{
    public override string ToString() => "StageIntent [details omitted]";
}

public sealed partial class AgentJournal
{
    private void InitializeStageIntents()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS stage_intents(
                job TEXT NOT NULL,fence INTEGER NOT NULL,reservation TEXT NOT NULL,version TEXT NOT NULL,
                root TEXT NOT NULL,relative_path TEXT NOT NULL,sha TEXT NOT NULL,size INTEGER NOT NULL,format TEXT NOT NULL,
                failure TEXT CHECK(failure IN ('LeaseRejected','LeaseExpired','ArchiveChanged','ArchiveUnavailable','Cancelled','StageFailed')),
                PRIMARY KEY(job,fence),UNIQUE(reservation,version),
                FOREIGN KEY(job,fence) REFERENCES leases(job,fence));
            """);
    }

    /// <summary>Persist before staging file I/O. Exact replays retain terminal failures, even after expiry or settlement.</summary>
    public StageIntent SaveStageIntent(StageIntent intent)
    {
        if (intent is null || intent.JobId == Guid.Empty || intent.FencingToken <= 0 ||
            intent.ReservationId == Guid.Empty || intent.ArchiveVersionId == Guid.Empty || intent.RootId == Guid.Empty ||
            intent.SizeBytes <= 0 || intent.Format is not ("dt" or "bak") || intent.Sha256 is not { Length: 64 } ||
            intent.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')))
            throw new ArgumentException("Invalid stage intent metadata.", nameof(intent));
        ValidateStageRelativePath(intent.RelativePath, intent.Format);
        intent = intent with { Sha256 = intent.Sha256.ToUpperInvariant() };
        using var transaction = _db.BeginTransaction();
        var existing = GetStageIntent(intent.JobId, intent.FencingToken);
        if (existing is not null)
        {
            if (existing != intent) throw new InvalidOperationException("Stage intent payload conflict.");
            transaction.Commit(); return existing;
        }
        using (var lease = Command("SELECT expiry,kind,settled FROM leases WHERE job=$0 AND fence=$1", intent.JobId.ToString(), intent.FencingToken))
        using (var reader = lease.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(1) != "ProvisionArchive" || reader.GetInt64(2) != 0 ||
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture) <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Stage intent requires an unexpired unsettled ProvisionArchive lease.");
        }
        using (var artifact = Command("SELECT COUNT(*) FROM artifacts WHERE job=$0 AND fence=$1 AND (reservation<>$2 OR version<>$3 OR sha<>$4 OR size<>$5 OR format<>$6)",
            intent.JobId.ToString(), intent.FencingToken, intent.ReservationId.ToString(), intent.ArchiveVersionId.ToString(), intent.Sha256, intent.SizeBytes, intent.Format))
            if ((long)artifact.ExecuteScalar()! != 0) throw new InvalidOperationException("Stage intent conflicts with artifact metadata.");
        Execute("INSERT INTO stage_intents(job,fence,reservation,version,root,relative_path,sha,size,format) VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8)",
            intent.JobId.ToString(), intent.FencingToken, intent.ReservationId.ToString(), intent.ArchiveVersionId.ToString(),
            intent.RootId.ToString(), intent.RelativePath, intent.Sha256, intent.SizeBytes, intent.Format);
        transaction.Commit(); return intent;
    }

    public StageIntent? GetStageIntent(Guid job, long fence)
    {
        using var command = Command("SELECT reservation,version,root,relative_path,sha,size,format FROM stage_intents WHERE job=$0 AND fence=$1", job.ToString(), fence);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(job, fence, Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6)) : null;
    }

    public void FailStageIntent(Guid job, long fence, string code)
    {
        if (code is not ("LeaseRejected" or "LeaseExpired" or "ArchiveChanged" or "ArchiveUnavailable" or "Cancelled" or "StageFailed"))
            throw new ArgumentException("Invalid stage failure code.", nameof(code));
        using var transaction = _db.BeginTransaction();
        if (GetStageIntent(job, fence) is null) throw new InvalidOperationException("Unknown stage intent.");
        var failure = StageIntentFailure(job, fence);
        if (failure is not null && failure != code) throw new InvalidOperationException("Stage intent already failed with a different reason.");
        Execute("UPDATE stage_intents SET failure=$2 WHERE job=$0 AND fence=$1 AND failure IS NULL", job.ToString(), fence, code);
        Execute("UPDATE artifacts SET state='Blocked',failure=$2 WHERE job=$0 AND fence=$1 AND state='Pending'", job.ToString(), fence, code);
        transaction.Commit();
    }

    public string? StageIntentFailure(Guid job, long fence)
    {
        using var command = Command("SELECT failure FROM stage_intents WHERE job=$0 AND fence=$1", job.ToString(), fence);
        return command.ExecuteScalar() as string;
    }

    private void ValidateStageArtifact(BuildSystem.Contracts.ReportStagedArtifact report)
    {
        var intent = GetStageIntent(report.JobId, report.FencingToken);
        if (intent is not null && (StageIntentFailure(report.JobId, report.FencingToken) is not null ||
            intent.ReservationId != report.ReservationId || intent.ArchiveVersionId != report.ArchiveVersionId ||
            intent.Sha256 != report.Sha256 || intent.SizeBytes != report.SizeBytes || intent.Format != report.Format))
            throw new InvalidOperationException("Artifact does not match an available stage intent.");
    }

    private static void ValidateStageRelativePath(string path, string format)
    {
        // Deliberately platform-independent: Linux must reject every Windows path escape too.
        if (string.IsNullOrWhiteSpace(path) || path.Length > 400 || path.StartsWith('/') || path.Contains('\\') ||
            path.Any(c => char.IsControl(c) || ":<>\"|?*".Contains(c)) || !path.EndsWith("." + format, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid canonical relative archive name.");
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.Length > 255 || part.EndsWith(' ') || part.EndsWith('.'))
                throw new ArgumentException("Invalid canonical relative archive segment.");
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("Windows reserved archive name.");
        }
    }
}
