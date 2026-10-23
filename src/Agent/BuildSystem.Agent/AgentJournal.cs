using System.Globalization;
using BuildSystem.Contracts;
using Microsoft.Data.Sqlite;

namespace BuildSystem.Agent;

public sealed record JournalLease(Guid JobId, long FencingToken, DateTimeOffset ExpiresAt, string Kind = "Unknown");

/// <summary>Durable lease metadata and allowlisted outboxes, never snapshots or credentials. Callers must serialize all access to this single-owner journal.</summary>
public sealed partial class AgentJournal : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly FileStream _lock;
    public AgentJournal(string dataDirectory)
    {
        if (!Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("DataDirectory must be absolute.", nameof(dataDirectory));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dataDirectory);
        else Directory.CreateDirectory(dataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _lock = new FileStream(Path.Combine(dataDirectory, "agent-journal.lock"), lockOptions);
        try
        {
        var path = Path.Combine(dataDirectory, "agent-journal.sqlite");
        // Precreate restrictive file before SQLite opens it; Windows ACLs must be provisioned by administrator.
        if (!OperatingSystem.IsWindows())
        {
            using var file = new FileStream(path, new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; CREATE TABLE IF NOT EXISTS leases(job TEXT NOT NULL,fence INTEGER NOT NULL,expiry TEXT NOT NULL,settled INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(job,fence)); CREATE TABLE IF NOT EXISTS events(id TEXT PRIMARY KEY,job TEXT NOT NULL,fence INTEGER NOT NULL,code TEXT NOT NULL,acked INTEGER NOT NULL DEFAULT 0,FOREIGN KEY(job,fence) REFERENCES leases(job,fence));");
        InitializeArtifacts();
        }
        catch { _db?.Dispose(); _lock.Dispose(); throw; }
    }

    private SqliteCommand Command(string sql, params object[] values)
    {
        var command = _db.CreateCommand(); command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$" + i, values[i]);
        return command;
    }
    private void Execute(string sql, params object[] values) { using var command = Command(sql, values); command.ExecuteNonQuery(); }
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public void StoreGrant(LeaseGrant grant)
    {
        if (grant.JobId == Guid.Empty || grant.FencingToken <= 0) throw new ArgumentException("Invalid grant identity.");
        using var transaction = _db.BeginTransaction();
        using var read = Command("SELECT expiry,kind FROM leases WHERE job=$0 AND fence=$1", grant.JobId.ToString(), grant.FencingToken);
        bool exists;
        using (var reader = read.ExecuteReader())
        {
            exists = reader.Read();
            if (exists && (reader.GetString(0) != Stamp(grant.ExpiresAt) ||
                (reader.GetString(1) != "Unknown" && reader.GetString(1) != grant.Kind)))
                throw new InvalidOperationException("Conflicting grant.");
            // Legacy Unknown rows cannot be upgraded by replaying a grant.
        }
        if (!exists)
        {
            using var active = Command("SELECT COUNT(*) FROM leases WHERE settled=0");
            if ((long)active.ExecuteScalar()! != 0) throw new InvalidOperationException("Unsettled lease blocks a new grant.");
            Execute("INSERT INTO leases(job,fence,expiry,kind) VALUES($0,$1,$2,$3)", grant.JobId.ToString(), grant.FencingToken, Stamp(grant.ExpiresAt), grant.Kind ?? "Unknown");
        }
        transaction.Commit();
    }

    public IReadOnlyList<JournalLease> Unsettled()
    {
        using var command = Command("SELECT job,fence,expiry,kind FROM leases WHERE settled=0 ORDER BY job,fence");
        using var reader = command.ExecuteReader(); var result = new List<JournalLease>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetString(3)));
        return result;
    }

    public void UpdateExpiry(Guid jobId, long fence, DateTimeOffset expiresAt)
    {
        using var command = Command("UPDATE leases SET expiry=$2 WHERE job=$0 AND fence=$1 AND settled=0", jobId.ToString(), fence, Stamp(expiresAt));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Unknown or settled lease.");
    }

    public void Enqueue(ReportAgentEvent report)
    {
        if (report.EventId == Guid.Empty || report.Code is not ("ProbeCompleted" or "WorkerUnsupported" or "LeaseOutcomeUnknown")) throw new ArgumentException("Invalid structured event.");
        using var transaction = _db.BeginTransaction();
        using var existing = Command("SELECT job,fence,code FROM events WHERE id=$0", report.EventId.ToString());
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != report.JobId.ToString() || reader.GetInt64(1) != report.FencingToken || reader.GetString(2) != report.Code) throw new InvalidOperationException("Event identity payload conflict.");
                reader.Close(); transaction.Commit(); return;
            }
        }
        using var lease = Command("SELECT COUNT(*) FROM leases WHERE job=$0 AND fence=$1 AND settled=0", report.JobId.ToString(), report.FencingToken);
        if ((long)lease.ExecuteScalar()! != 1) throw new InvalidOperationException("Unknown or settled lease.");
        using var slot = Command("SELECT COUNT(*) FROM events WHERE job=$0 AND fence=$1 AND code=$2", report.JobId.ToString(), report.FencingToken, report.Code);
        if ((long)slot.ExecuteScalar()! != 0) throw new InvalidOperationException("This lease already has an event with that code; reuse its EventId.");
        Execute("INSERT INTO events(id,job,fence,code) VALUES($0,$1,$2,$3)", report.EventId.ToString(), report.JobId.ToString(), report.FencingToken, report.Code);
        transaction.Commit();
    }

    public bool IsSettled(Guid jobId, long fence)
    {
        using var command = Command("SELECT settled FROM leases WHERE job=$0 AND fence=$1", jobId.ToString(), fence);
        return command.ExecuteScalar() is long settled && settled == 1;
    }

    public bool HasEvent(Guid jobId, long fence, string code)
    {
        using var command = Command("SELECT COUNT(*) FROM events WHERE job=$0 AND fence=$1 AND code=$2", jobId.ToString(), fence, code);
        return (long)command.ExecuteScalar()! != 0;
    }

    public IReadOnlyList<ReportAgentEvent> Pending()
    {
        using var command = Command("SELECT id,job,fence,code FROM events WHERE acked=0 ORDER BY rowid");
        using var reader = command.ExecuteReader(); var result = new List<ReportAgentEvent>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2), reader.GetString(3)));
        return result;
    }

    public void Acknowledge(Guid eventId)
    {
        using var transaction = _db.BeginTransaction();
        using var read = Command("SELECT job,fence,code FROM events WHERE id=$0", eventId.ToString());
        string job; long fence; string code;
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Unknown event acknowledgement.");
            job = reader.GetString(0); fence = reader.GetInt64(1); code = reader.GetString(2);
        }
        Execute("UPDATE events SET acked=1 WHERE id=$0", eventId.ToString());
        if (code == "ProbeCompleted") Execute("UPDATE leases SET settled=1 WHERE job=$0 AND fence=$1 AND NOT EXISTS (SELECT 1 FROM artifacts WHERE job=$0 AND fence=$1)", job, fence);
        transaction.Commit();
    }

    /// <summary>Explicit operator override after investigation; retains diagnostic rows and suppresses old outbox replay.</summary>
    public void MarkSettled(Guid jobId, long fence)
    {
        using var transaction = _db.BeginTransaction();
        Execute("UPDATE leases SET settled=1 WHERE job=$0 AND fence=$1", jobId.ToString(), fence);
        Execute("UPDATE events SET acked=1 WHERE job=$0 AND fence=$1", jobId.ToString(), fence);
        Execute("UPDATE artifacts SET state='Blocked',failure='LeaseRejected' WHERE job=$0 AND fence=$1 AND state='Pending'", jobId.ToString(), fence);
        transaction.Commit();
    }
    public void Dispose() { _db.Dispose(); _lock.Dispose(); }
}
