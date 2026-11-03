using System.Globalization;
using BuildSystem.Contracts;
using Microsoft.Data.Sqlite;

namespace BuildSystem.Agent;

/// <summary>Independent durable observation-only outbox. No lease, creation, settlement or ownership transitions.</summary>
public sealed partial class AgentJournal
{
    private const string ObservationAssignmentColumns =
        "request,restore_intent,agent,operation,environment,stage_job,fence,reservation,incarnation,cluster,sql_server,rac_mapping,sql_mapping";
    private const string JoinedObservationColumns =
        "a.request,a.restore_intent,a.agent,a.operation,a.environment,a.stage_job,a.fence,a.reservation,a.incarnation,a.cluster,a.sql_server,a.rac_mapping,a.sql_mapping," +
        "r.receipt,r.rac_observation,r.sql_observation,r.outcome,r.observed_at,r.acked";

    private void InitializeTargetObservations()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS target_observation_assignments(
                request TEXT PRIMARY KEY NOT NULL,restore_intent TEXT NOT NULL,agent TEXT NOT NULL,
                operation TEXT NOT NULL,environment TEXT NOT NULL,stage_job TEXT NOT NULL,
                fence INTEGER NOT NULL CHECK(fence>0),reservation TEXT NOT NULL,
                incarnation TEXT NOT NULL,cluster TEXT NOT NULL,sql_server TEXT NOT NULL,
                rac_mapping TEXT NOT NULL,sql_mapping TEXT NOT NULL,
                CHECK(request<>'00000000-0000-0000-0000-000000000000'),
                CHECK(restore_intent<>'00000000-0000-0000-0000-000000000000'),
                CHECK(agent<>'00000000-0000-0000-0000-000000000000'),
                CHECK(operation<>'00000000-0000-0000-0000-000000000000'),
                CHECK(environment<>'00000000-0000-0000-0000-000000000000'),
                CHECK(stage_job<>'00000000-0000-0000-0000-000000000000'),
                CHECK(reservation<>'00000000-0000-0000-0000-000000000000'),
                CHECK(incarnation<>'00000000-0000-0000-0000-000000000000'),
                CHECK(cluster<>'00000000-0000-0000-0000-000000000000'),
                CHECK(sql_server<>'00000000-0000-0000-0000-000000000000'),
                CHECK(rac_mapping<>'00000000-0000-0000-0000-000000000000'),
                CHECK(sql_mapping<>'00000000-0000-0000-0000-000000000000'));
            CREATE TABLE IF NOT EXISTS target_observation_reports(
                receipt TEXT PRIMARY KEY NOT NULL,
                request TEXT NOT NULL UNIQUE REFERENCES target_observation_assignments(request),
                rac_observation TEXT NOT NULL,sql_observation TEXT NOT NULL,
                outcome INTEGER NOT NULL CHECK(outcome IN (0,1,2,3)),
                observed_at TEXT NOT NULL CHECK(length(observed_at)>0),
                acked INTEGER NOT NULL DEFAULT 0 CHECK(acked IN (0,1)),
                CHECK(receipt<>'00000000-0000-0000-0000-000000000000'),
                CHECK(rac_observation<>'00000000-0000-0000-0000-000000000000'),
                CHECK(sql_observation<>'00000000-0000-0000-0000-000000000000'),
                CHECK(rac_observation<>sql_observation));
            """);
    }

    private static void ValidateObservationAssignment(TargetObservationAssignment? assignment)
    {
        if (assignment is null || new[] {
            assignment.ObservationRequestId, assignment.RestoreIntentId, assignment.AgentId,
            assignment.OperationId, assignment.EnvironmentId, assignment.StageJobId,
            assignment.ReservationId, assignment.TargetIncarnationId, assignment.CanonicalClusterId,
            assignment.CanonicalSqlServerId, assignment.RacEndpointMappingId, assignment.SqlEndpointMappingId
        }.Any(id => id == Guid.Empty) || assignment.FencingToken <= 0)
            throw new ArgumentException("Invalid observation assignment.", nameof(assignment));
    }

    private static void ValidateObservationReport(ReportTargetObservation? report)
    {
        if (report is null) throw new ArgumentException("Invalid observation report.", nameof(report));
        ValidateObservationAssignment(report.Assignment);
        if (report.ReceiptId == Guid.Empty || report.RacObservationId == Guid.Empty ||
            report.SqlObservationId == Guid.Empty || report.RacObservationId == report.SqlObservationId ||
            !Enum.IsDefined(report.Outcome) || report.ObservedAt == default ||
            report.ObservedAt.UtcDateTime.Year is < 1 or > 9999)
            throw new ArgumentException("Invalid observation report.", nameof(report));
    }

    private static Guid ObservationGuid(SqliteDataReader reader, int index)
    {
        var text = reader.GetString(index);
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty ||
            !string.Equals(text, value.ToString("D"), StringComparison.Ordinal))
            throw new InvalidOperationException("Corrupt observation identity.");
        return value;
    }

    private static long ObservationInteger(SqliteDataReader reader, int index)
    {
        if (reader.GetValue(index) is not long value)
            throw new InvalidOperationException("Corrupt observation integer.");
        return value;
    }

    private static TargetObservationAssignment ReadObservationAssignment(SqliteDataReader reader)
    {
        var result = new TargetObservationAssignment(
            ObservationGuid(reader, 0), ObservationGuid(reader, 1), ObservationGuid(reader, 2),
            ObservationGuid(reader, 3), ObservationGuid(reader, 4), ObservationGuid(reader, 5),
            ObservationInteger(reader, 6), ObservationGuid(reader, 7), ObservationGuid(reader, 8),
            ObservationGuid(reader, 9), ObservationGuid(reader, 10), ObservationGuid(reader, 11),
            ObservationGuid(reader, 12));
        if (result.FencingToken <= 0) throw new InvalidOperationException("Corrupt observation fence.");
        return result;
    }

    private static ReportTargetObservation ReadObservationReport(SqliteDataReader reader)
    {
        var assignment = ReadObservationAssignment(reader);
        var outcome = ObservationInteger(reader, 16);
        if (outcome is < 0 or > 3) throw new InvalidOperationException("Corrupt observation outcome.");
        var stamp = reader.GetString(17);
        if (!DateTimeOffset.TryParseExact(stamp, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var observedAt) || observedAt == default ||
            observedAt.Offset != TimeSpan.Zero || Stamp(observedAt) != stamp)
            throw new InvalidOperationException("Corrupt observation timestamp.");
        if (ObservationInteger(reader, 18) is not (0 or 1)) throw new InvalidOperationException("Corrupt observation ack status.");
        var result = new ReportTargetObservation(ObservationGuid(reader, 13), assignment,
            ObservationGuid(reader, 14), ObservationGuid(reader, 15), (TargetObservationOutcome)outcome, observedAt);
        if (result.RacObservationId == result.SqlObservationId)
            throw new InvalidOperationException("Corrupt observation pair.");
        return result;
    }

    /// <summary>Persist correlation only. Does not verify server authority or acquire an execution lease.</summary>
    public TargetObservationAssignment SaveTargetObservationAssignment(TargetObservationAssignment assignment)
    {
        ValidateObservationAssignment(assignment);
        using var transaction = _db.BeginTransaction();
        var existing = GetTargetObservationAssignment(assignment.ObservationRequestId);
        if (existing is not null)
        {
            if (existing != assignment) throw new InvalidOperationException("Observation assignment conflict.");
            transaction.Commit(); return existing;
        }
        Execute("""
            INSERT INTO target_observation_assignments(request,restore_intent,agent,operation,environment,stage_job,fence,reservation,incarnation,cluster,sql_server,rac_mapping,sql_mapping)
            VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
            """, assignment.ObservationRequestId.ToString(), assignment.RestoreIntentId.ToString(),
            assignment.AgentId.ToString(), assignment.OperationId.ToString(), assignment.EnvironmentId.ToString(),
            assignment.StageJobId.ToString(), assignment.FencingToken, assignment.ReservationId.ToString(),
            assignment.TargetIncarnationId.ToString(), assignment.CanonicalClusterId.ToString(),
            assignment.CanonicalSqlServerId.ToString(), assignment.RacEndpointMappingId.ToString(),
            assignment.SqlEndpointMappingId.ToString());
        transaction.Commit(); return assignment;
    }

    public TargetObservationAssignment? GetTargetObservationAssignment(Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Invalid observation request.", nameof(requestId));
        using var command = Command($"SELECT {ObservationAssignmentColumns} FROM target_observation_assignments WHERE request=$0", requestId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadObservationAssignment(reader) : null;
    }

    /// <summary>One successful paired historical report per request forever, including after acknowledgement.</summary>
    public void EnqueueTargetObservation(ReportTargetObservation report)
    {
        ValidateObservationReport(report);
        using var transaction = _db.BeginTransaction();
        var saved = GetTargetObservationAssignment(report.Assignment.ObservationRequestId);
        if (saved is null || saved != report.Assignment)
            throw new InvalidOperationException("Unknown or conflicting observation assignment.");
        using (var command = Command($"SELECT {JoinedObservationColumns} FROM target_observation_reports r JOIN target_observation_assignments a ON a.request=r.request WHERE r.receipt=$0 OR r.request=$1",
            report.ReceiptId.ToString(), report.Assignment.ObservationRequestId.ToString()))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                var existing = ReadObservationReport(reader);
                // DateTimeOffset equality compares instants; canonical UTC stamps preserve exact replay.
                if (reader.Read() || existing != report)
                    throw new InvalidOperationException("Observation receipt or request conflict.");
                transaction.Commit(); return;
            }
        }
        Execute("""
            INSERT INTO target_observation_reports(receipt,request,rac_observation,sql_observation,outcome,observed_at)
            VALUES($0,$1,$2,$3,$4,$5)
            """, report.ReceiptId.ToString(), report.Assignment.ObservationRequestId.ToString(),
            report.RacObservationId.ToString(), report.SqlObservationId.ToString(),
            (long)report.Outcome, Stamp(report.ObservedAt));
        transaction.Commit();
    }

    /// <summary>Returns retained historical evidence even after ACK; never establishes fresh absence.</summary>
    public ReportTargetObservation? GetTargetObservationReport(Guid requestId)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Invalid observation request.", nameof(requestId));
        using var command = Command($"SELECT {JoinedObservationColumns} FROM target_observation_reports r JOIN target_observation_assignments a ON a.request=r.request WHERE r.request=$0", requestId.ToString());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadObservationReport(reader) : null;
    }

    /// <summary>Ordered bounded snapshot, never an executable queue or a freshness grant.</summary>
    public IReadOnlyList<ReportTargetObservation> PendingTargetObservations(int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var command = Command($"SELECT {JoinedObservationColumns} FROM target_observation_reports r JOIN target_observation_assignments a ON a.request=r.request WHERE r.acked=0 ORDER BY r.rowid LIMIT $0", limit);
        using var reader = command.ExecuteReader();
        var results = new List<ReportTargetObservation>();
        while (reader.Read()) results.Add(ReadObservationReport(reader));
        return results.AsReadOnly();
    }

    /// <summary>Acknowledges delivery only; retains the assignment and receipt for replay conflict checks.</summary>
    public void AckTargetObservation(Guid receiptId)
    {
        if (receiptId == Guid.Empty) throw new ArgumentException("Invalid observation receipt.", nameof(receiptId));
        using var transaction = _db.BeginTransaction();
        using (var command = Command($"SELECT {JoinedObservationColumns} FROM target_observation_reports r JOIN target_observation_assignments a ON a.request=r.request WHERE r.receipt=$0", receiptId.ToString()))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Unknown observation receipt.");
            _ = ReadObservationReport(reader);
        }
        Execute("UPDATE target_observation_reports SET acked=1 WHERE receipt=$0", receiptId.ToString());
        transaction.Commit();
    }
}
