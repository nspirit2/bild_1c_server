using Microsoft.Data.Sqlite;

namespace BuildSystem.Agent;

/// <summary>Immutable local pins, not creation authority or evidence of external ownership.
/// Canonical IDs must come from trusted configuration; this journal does not resolve aliases.</summary>
public sealed record TargetCreationIntent(Guid OperationId, Guid EnvironmentId, Guid JobId, long FencingToken,
    Guid ReservationId, Guid TargetIncarnationId, Guid CanonicalClusterId, Guid CanonicalSqlServerId,
    string InfobaseName, string SqlDatabaseName)
{
    public override string ToString() => "TargetCreationIntent [details omitted]";
}

public enum TargetCreationState { IntentRecorded, CreationInFlight, ReconciliationRequired }

/// <summary>Local unresolved metadata only; no state proves ownership or authorizes execution.</summary>
public sealed record TargetCreationRecord(TargetCreationIntent Intent, TargetCreationState State, long Revision,
    Guid? AttemptId, long? AttemptExpectedRevision, string? QuarantineReason)
{
    public override string ToString() => $"TargetCreationRecord [{State}; details omitted]";
}

/// <summary>A persistence receipt, NEVER execution authorization. A replay must not repeat mutations.</summary>
public sealed record TargetCreationAttemptReceipt(TargetCreationRecord Record, bool NewlyRecorded)
{
    public override string ToString() => "TargetCreationAttemptReceipt [not execution authorization; details omitted]";
}

public sealed partial class AgentJournal
{
    private void InitializeTargetCreationIntents()
    {
        // Intentionally independent of leases. Neither lease settlement nor legacy stage grants
        // release these local slots. There is no success, ownership, reset or deletion transition.
        Execute("""
            CREATE TABLE IF NOT EXISTS target_creation_intents(
                operation TEXT PRIMARY KEY,environment TEXT NOT NULL,job TEXT NOT NULL,fence INTEGER NOT NULL CHECK(fence>0),
                reservation TEXT NOT NULL,incarnation TEXT NOT NULL,cluster TEXT NOT NULL,sql_server TEXT NOT NULL,
                infobase TEXT NOT NULL,sql_database TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('IntentRecorded','CreationInFlight','ReconciliationRequired')),
                revision INTEGER NOT NULL CHECK(revision>0),attempt TEXT,attempt_revision INTEGER,
                reason TEXT CHECK(reason IN ('OutcomeUnknown','TargetCreationFailed','TargetConflict','ObservationMismatch','LeaseRejected','LeaseExpired','Cancelled')),
                UNIQUE(sql_server,sql_database),UNIQUE(cluster,infobase),
                CHECK((attempt IS NULL AND attempt_revision IS NULL) OR (attempt IS NOT NULL AND attempt_revision IS NOT NULL AND attempt_revision>0)),
                CHECK(state<>'IntentRecorded' OR (attempt IS NULL AND reason IS NULL)),
                CHECK(state<>'CreationInFlight' OR (attempt IS NOT NULL AND reason IS NULL)),
                CHECK(state<>'ReconciliationRequired' OR reason IS NOT NULL));
            """);
    }

    /// <summary>Records immutable local pins. Exact replay in every state preserves all barriers.
    /// Does not validate fresh authority, create resources, or establish cross-agent exclusion.</summary>
    public TargetCreationRecord SaveTargetCreationIntent(TargetCreationIntent intent)
    {
        if (intent is null || new[] { intent.OperationId, intent.EnvironmentId, intent.JobId, intent.ReservationId,
                intent.TargetIncarnationId, intent.CanonicalClusterId, intent.CanonicalSqlServerId }.Any(id => id == Guid.Empty) ||
            intent.FencingToken <= 0 || !ValidTargetCreationName(intent.InfobaseName, intent.ReservationId) ||
            !ValidTargetCreationName(intent.SqlDatabaseName, intent.ReservationId))
            throw new ArgumentException("Invalid target creation intent.", nameof(intent));
        using var transaction = _db.BeginTransaction();
        var existing = GetTargetCreationIntent(intent.OperationId);
        if (existing is not null)
        {
            if (existing.Intent != intent) throw new InvalidOperationException("Target creation intent payload conflict.");
            transaction.Commit(); return existing;
        }
        using (var slots = Command("SELECT COUNT(*) FROM target_creation_intents WHERE (sql_server=$0 AND sql_database=$1) OR (cluster=$2 AND infobase=$3)",
            intent.CanonicalSqlServerId.ToString(), intent.SqlDatabaseName, intent.CanonicalClusterId.ToString(), intent.InfobaseName))
            if ((long)slots.ExecuteScalar()! != 0) throw new InvalidOperationException("Target creation slot conflict.");
        Execute("""
            INSERT INTO target_creation_intents(operation,environment,job,fence,reservation,incarnation,cluster,sql_server,infobase,sql_database,state,revision)
            VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,'IntentRecorded',1)
            """, intent.OperationId.ToString(), intent.EnvironmentId.ToString(), intent.JobId.ToString(), intent.FencingToken,
            intent.ReservationId.ToString(), intent.TargetIncarnationId.ToString(), intent.CanonicalClusterId.ToString(),
            intent.CanonicalSqlServerId.ToString(), intent.InfobaseName, intent.SqlDatabaseName);
        transaction.Commit(); return new(intent, TargetCreationState.IntentRecorded, 1, null, null, null);
    }

    private static bool ValidTargetCreationName(string? name, Guid reservation) =>
        string.Equals(name, "bs_test_" + reservation.ToString("N"), StringComparison.Ordinal);

    private const string TargetCreationColumns = "operation,environment,job,fence,reservation,incarnation,cluster,sql_server,infobase,sql_database,state,revision,attempt,attempt_revision,reason";

    public TargetCreationRecord? GetTargetCreationIntent(Guid operationId)
    {
        ValidateTargetCreationOperation(operationId);
        using var command = Command($"SELECT {TargetCreationColumns} FROM target_creation_intents WHERE operation=$0", operationId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTargetCreationRecord(reader) : null;
    }

    /// <summary>Bounded reconciliation inventory, not an executable queue. Opening/listing never changes state.</summary>
    public IReadOnlyList<TargetCreationRecord> ListUnresolvedTargetCreationIntents(int limit = 1000)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 1000.");
        using var command = Command($"SELECT {TargetCreationColumns} FROM target_creation_intents ORDER BY rowid LIMIT $0", limit);
        using var reader = command.ExecuteReader(); var result = new List<TargetCreationRecord>();
        while (reader.Read()) result.Add(ReadTargetCreationRecord(reader));
        return result;
    }

    /// <summary>Durably records the one attempt before any external mutation. This local receipt is not
    /// permission to execute: fresh external authority and reconciliation are separate future requirements.</summary>
    public TargetCreationAttemptReceipt TryBeginTargetCreation(Guid operationId, long expectedRevision, Guid attemptId)
    {
        ValidateTargetCreationOperation(operationId);
        if (expectedRevision <= 0 || attemptId == Guid.Empty) throw new ArgumentException("Invalid target creation attempt.");
        using var transaction = _db.BeginTransaction();
        var existing = GetTargetCreationIntent(operationId) ?? throw new InvalidOperationException("Unknown target creation intent.");
        if (existing.AttemptId == attemptId && existing.AttemptExpectedRevision == expectedRevision)
        {
            transaction.Commit(); return new(existing, false);
        }
        if (existing.State != TargetCreationState.IntentRecorded || existing.Revision != expectedRevision)
            throw new InvalidOperationException("Target creation attempt state or revision conflict.");
        var next = checked(existing.Revision + 1);
        using var update = Command("UPDATE target_creation_intents SET state='CreationInFlight',revision=$2,attempt=$3,attempt_revision=$1 WHERE operation=$0 AND revision=$1 AND state='IntentRecorded' AND attempt IS NULL",
            operationId.ToString(), expectedRevision, next, attemptId.ToString());
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Target creation attempt revision conflict.");
        transaction.Commit();
        return new(existing with { State = TargetCreationState.CreationInFlight, Revision = next, AttemptId = attemptId, AttemptExpectedRevision = expectedRevision }, true);
    }

    /// <summary>Fail-closed quarantine retains both slots and the attempt. No automatic recovery follows.</summary>
    public TargetCreationRecord QuarantineTargetCreation(Guid operationId, long expectedRevision, string reason)
    {
        ValidateTargetCreationOperation(operationId);
        if (expectedRevision <= 0 || reason is not ("OutcomeUnknown" or "TargetCreationFailed" or "TargetConflict" or
            "ObservationMismatch" or "LeaseRejected" or "LeaseExpired" or "Cancelled"))
            throw new ArgumentException("Invalid target creation quarantine metadata.");
        using var transaction = _db.BeginTransaction();
        var existing = GetTargetCreationIntent(operationId) ?? throw new InvalidOperationException("Unknown target creation intent.");
        if (existing.Revision != expectedRevision || existing.State == TargetCreationState.ReconciliationRequired)
            throw new InvalidOperationException("Target creation quarantine state or revision conflict.");
        var next = checked(existing.Revision + 1);
        using var update = Command("UPDATE target_creation_intents SET state='ReconciliationRequired',revision=$2,reason=$3 WHERE operation=$0 AND revision=$1 AND state<>'ReconciliationRequired'",
            operationId.ToString(), expectedRevision, next, reason);
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Target creation quarantine revision conflict.");
        transaction.Commit();
        return existing with { State = TargetCreationState.ReconciliationRequired, Revision = next, QuarantineReason = reason };
    }

    private static void ValidateTargetCreationOperation(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Invalid target creation operation.", nameof(operationId));
    }

    private static TargetCreationRecord ReadTargetCreationRecord(SqliteDataReader reader) => new(
        new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetInt64(3),
            Guid.Parse(reader.GetString(4)), Guid.Parse(reader.GetString(5)), Guid.Parse(reader.GetString(6)), Guid.Parse(reader.GetString(7)),
            reader.GetString(8), reader.GetString(9)), Enum.Parse<TargetCreationState>(reader.GetString(10)), reader.GetInt64(11),
        reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)), reader.IsDBNull(13) ? null : reader.GetInt64(13),
        reader.IsDBNull(14) ? null : reader.GetString(14));
}
