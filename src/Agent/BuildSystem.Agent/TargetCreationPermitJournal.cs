using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BuildSystem.Agent;

/// <summary>Inert pins parsed by a future verified transport, not a server-validated receipt or execution authority.</summary>
public sealed record TargetCreationPermitBinding(Guid PermitId, Guid OperationId, Guid AttemptId,
    Guid RestoreIntentId, Guid ArtifactId, Guid AgentId, DateTimeOffset IssuedAt, DateTimeOffset StartBefore)
{
    public override string ToString() => "TargetCreationPermitBinding [not execution authorization; details omitted]";
}

/// <summary>Local persistence only. Neither this receipt nor a recorded binding authorizes execution.</summary>
public sealed record TargetCreationPermitBindingReceipt(TargetCreationPermitBinding Binding, bool NewlyRecorded)
{
    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetCreationPermitBindingReceipt [not execution authorization; details omitted]";
}

public sealed partial class AgentJournal
{
    private void InitializeTargetCreationPermitBindings()
    {
        using (var foreignKeys = Command("PRAGMA foreign_keys"))
            if (foreignKeys.ExecuteScalar() is not long enabled || enabled != 1)
                throw new InvalidOperationException("Target creation bindings require SQLite foreign keys.");
        // No cascade, lease dependency, update or delete API: these identity slots remain occupied.
        Execute("""
            CREATE TABLE IF NOT EXISTS target_creation_permit_bindings(
                permit TEXT NOT NULL PRIMARY KEY CHECK(length(permit)=36 AND permit=lower(permit) AND permit<>'00000000-0000-0000-0000-000000000000'),
                operation TEXT NOT NULL UNIQUE REFERENCES target_creation_intents(operation) CHECK(length(operation)=36 AND operation=lower(operation) AND operation<>'00000000-0000-0000-0000-000000000000'),
                attempt TEXT NOT NULL UNIQUE CHECK(length(attempt)=36 AND attempt=lower(attempt) AND attempt<>'00000000-0000-0000-0000-000000000000'),
                restore_intent TEXT NOT NULL CHECK(length(restore_intent)=36 AND restore_intent=lower(restore_intent) AND restore_intent<>'00000000-0000-0000-0000-000000000000'),
                artifact TEXT NOT NULL CHECK(length(artifact)=36 AND artifact=lower(artifact) AND artifact<>'00000000-0000-0000-0000-000000000000'),
                agent TEXT NOT NULL CHECK(length(agent)=36 AND agent=lower(agent) AND agent<>'00000000-0000-0000-0000-000000000000'),
                issued_at TEXT NOT NULL,start_before TEXT NOT NULL);
            """);
    }

    /// <summary>Atomically pins a binding to an exact existing intent, without beginning or changing an attempt.
    /// New pins require the initial local state; exact replay preserves every later state, including quarantine.
    /// No local clock/deadline decision or server authorization validation occurs here. A future execution
    /// handshake must separately verify fresh authority; TryBeginTargetCreation alone does not grant it.</summary>
    public TargetCreationPermitBindingReceipt SaveTargetCreationPermitBinding(
        TargetCreationIntent expectedIntent, TargetCreationPermitBinding binding)
    {
        ValidateTargetCreationIntent(expectedIntent);
        if (!ValidTargetCreationPermitBinding(binding) || binding.OperationId != expectedIntent.OperationId)
            throw new ArgumentException("Invalid target creation permit binding.", nameof(binding));
        using var transaction = _db.BeginTransaction();
        var local = GetTargetCreationIntent(binding.OperationId)
            ?? throw new InvalidOperationException("Unknown target creation intent.");
        if (local.Intent != expectedIntent)
            throw new InvalidOperationException("Target creation intent payload conflict.");
        var existing = GetTargetCreationPermitBinding(binding.OperationId);
        if (existing is not null)
        {
            if (existing != binding) throw new InvalidOperationException("Target creation permit binding payload conflict.");
            transaction.Commit();
            return new(existing, false);
        }
        if (local.State != TargetCreationState.IntentRecorded || local.Revision != 1 || local.AttemptId is not null ||
            local.AttemptExpectedRevision is not null)
            throw new InvalidOperationException("New target creation permit binding requires an initial local intent.");
        using (var slots = Command("SELECT COUNT(*) FROM target_creation_permit_bindings WHERE permit=$0 OR attempt=$1",
            binding.PermitId.ToString(), binding.AttemptId.ToString()))
            if ((long)slots.ExecuteScalar()! != 0)
                throw new InvalidOperationException("Target creation permit or attempt already bound.");
        Execute("""
            INSERT INTO target_creation_permit_bindings(permit,operation,attempt,restore_intent,artifact,agent,issued_at,start_before)
            VALUES($0,$1,$2,$3,$4,$5,$6,$7)
            """, binding.PermitId.ToString(), binding.OperationId.ToString(), binding.AttemptId.ToString(),
            binding.RestoreIntentId.ToString(), binding.ArtifactId.ToString(), binding.AgentId.ToString(),
            Stamp(binding.IssuedAt), Stamp(binding.StartBefore));
        transaction.Commit();
        return new(binding, true);
    }

    /// <summary>Validated local metadata only; raw database contents are trusted storage, never authority.</summary>
    public TargetCreationPermitBinding? GetTargetCreationPermitBinding(Guid operationId)
    {
        ValidateTargetCreationOperation(operationId);
        TargetCreationPermitBinding binding;
        using (var command = Command("SELECT permit,operation,attempt,restore_intent,artifact,agent,issued_at,start_before FROM target_creation_permit_bindings WHERE operation=$0", operationId.ToString()))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            binding = ReadTargetCreationPermitBinding(reader);
        }
        var local = GetTargetCreationIntent(operationId)
            ?? throw new InvalidOperationException("Target creation permit binding has no local intent.");
        if (binding.OperationId != local.Intent.OperationId || local.AttemptId is { } attempt && attempt != binding.AttemptId)
            throw new InvalidOperationException("Persisted target creation permit binding conflicts with local intent.");
        return binding;
    }

    private static bool ValidTargetCreationPermitBinding(TargetCreationPermitBinding? binding) => binding is not null &&
        new[] { binding.PermitId, binding.OperationId, binding.AttemptId, binding.RestoreIntentId, binding.ArtifactId, binding.AgentId }
            .All(id => id != Guid.Empty) &&
        binding.IssuedAt.Offset == TimeSpan.Zero && binding.StartBefore.Offset == TimeSpan.Zero &&
        binding.IssuedAt <= DateTimeOffset.MaxValue.AddMinutes(-5) &&
        binding.StartBefore == binding.IssuedAt.AddMinutes(5);

    private static TargetCreationPermitBinding ReadTargetCreationPermitBinding(SqliteDataReader reader)
    {
        try
        {
            var ids = new Guid[6];
            for (var index = 0; index < ids.Length; index++)
            {
                var text = reader.GetString(index);
                if (!Guid.TryParseExact(text, "D", out ids[index]) || text != ids[index].ToString())
                    throw new InvalidOperationException("Invalid persisted target creation permit binding.");
            }
            if (!DateTimeOffset.TryParseExact(reader.GetString(6), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issuedAt) ||
                !DateTimeOffset.TryParseExact(reader.GetString(7), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startBefore))
                throw new InvalidOperationException("Invalid persisted target creation permit binding.");
            var binding = new TargetCreationPermitBinding(ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], issuedAt, startBefore);
            if (!ValidTargetCreationPermitBinding(binding))
                throw new InvalidOperationException("Invalid persisted target creation permit binding.");
            return binding;
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or ArgumentException or OverflowException)
        {
            throw new InvalidOperationException("Invalid persisted target creation permit binding.", error);
        }
    }
}
