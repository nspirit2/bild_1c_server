using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BuildSystem.Agent;

/// <summary>Inert pins parsed by a future verified transport, not a server-validated receipt or execution authority.</summary>
public sealed record TargetCreationPermitBinding(Guid PermitId, Guid OperationId, Guid AttemptId,
    Guid RestoreIntentId, Guid ArtifactId, Guid AgentId, DateTimeOffset IssuedAt, DateTimeOffset StartBefore)
{
    // Optional immutable identity pins only, never credential material or local execution authority.
    // A future verified transport must establish the authoritative credential/mapping binding.
    public Guid? CredentialId { get; init; }
    public Guid? EndpointMappingId { get; init; }

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
            -- Additive migration: legacy bindings remain explicitly unbound; never infer pins.
            CREATE TABLE IF NOT EXISTS target_creation_permit_credential_pins(
                permit TEXT NOT NULL PRIMARY KEY REFERENCES target_creation_permit_bindings(permit) ON DELETE RESTRICT,
                credential TEXT NOT NULL CHECK(length(credential)=36 AND credential=lower(credential) AND
                    substr(credential,9,1)='-' AND substr(credential,14,1)='-' AND substr(credential,19,1)='-' AND substr(credential,24,1)='-' AND
                    length(replace(credential,'-',''))=32 AND replace(credential,'-','') NOT GLOB '*[^0-9a-f]*' AND
                    credential<>'00000000-0000-0000-0000-000000000000'),
                endpoint_mapping TEXT NOT NULL CHECK(length(endpoint_mapping)=36 AND endpoint_mapping=lower(endpoint_mapping) AND
                    substr(endpoint_mapping,9,1)='-' AND substr(endpoint_mapping,14,1)='-' AND substr(endpoint_mapping,19,1)='-' AND substr(endpoint_mapping,24,1)='-' AND
                    length(replace(endpoint_mapping,'-',''))=32 AND replace(endpoint_mapping,'-','') NOT GLOB '*[^0-9a-f]*' AND
                    endpoint_mapping<>'00000000-0000-0000-0000-000000000000'));
            -- REPLACE can bypass delete triggers when recursive_triggers is disabled.
            CREATE TRIGGER IF NOT EXISTS target_creation_permit_credential_pins_no_reinsert
                BEFORE INSERT ON target_creation_permit_credential_pins
                WHEN EXISTS(SELECT 1 FROM target_creation_permit_credential_pins WHERE permit=NEW.permit)
                BEGIN SELECT RAISE(ABORT,'Target creation permit credential pins are immutable.'); END;
            CREATE TRIGGER IF NOT EXISTS target_creation_permit_credential_pins_no_update
                BEFORE UPDATE ON target_creation_permit_credential_pins
                BEGIN SELECT RAISE(ABORT,'Target creation permit credential pins are immutable.'); END;
            CREATE TRIGGER IF NOT EXISTS target_creation_permit_credential_pins_no_delete
                BEFORE DELETE ON target_creation_permit_credential_pins
                BEGIN SELECT RAISE(ABORT,'Target creation permit credential pins are immutable.'); END;
            """);
    }

    /// <summary>Atomically pins a binding to an exact existing intent, without beginning or changing an attempt.
    /// New pins require the initial local state; exact replay preserves every later state, including quarantine.
    /// Credential pins cannot be changed or retrofitted onto an existing binding through this API.
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
        if (binding.CredentialId is { } credentialId && binding.EndpointMappingId is { } endpointMappingId)
            Execute("""
                INSERT INTO target_creation_permit_credential_pins(permit,credential,endpoint_mapping)
                VALUES($0,$1,$2)
                """, binding.PermitId.ToString(), credentialId.ToString(), endpointMappingId.ToString());
        transaction.Commit();
        return new(binding, true);
    }

    /// <summary>Validated local metadata only; raw database contents are trusted storage, never authority.</summary>
    public TargetCreationPermitBinding? GetTargetCreationPermitBinding(Guid operationId)
    {
        ValidateTargetCreationOperation(operationId);
        TargetCreationPermitBinding binding;
        using (var command = Command("""
            SELECT b.permit,b.operation,b.attempt,b.restore_intent,b.artifact,b.agent,b.issued_at,b.start_before,
                p.credential,p.endpoint_mapping,p.permit
            FROM target_creation_permit_bindings b
            LEFT JOIN target_creation_permit_credential_pins p ON p.permit=b.permit
            WHERE b.operation=$0
            """, operationId.ToString()))
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
        (binding.CredentialId is null && binding.EndpointMappingId is null ||
            binding.CredentialId is { } credentialId && credentialId != Guid.Empty &&
            binding.EndpointMappingId is { } endpointMappingId && endpointMappingId != Guid.Empty) &&
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
            Guid? credentialId = null;
            Guid? endpointMappingId = null;
            var hasCredentialPins = !reader.IsDBNull(10);
            if (hasCredentialPins && (reader.IsDBNull(8) || reader.IsDBNull(9)) ||
                !hasCredentialPins && (!reader.IsDBNull(8) || !reader.IsDBNull(9)))
                throw new InvalidOperationException("Invalid persisted target creation permit binding.");
            if (hasCredentialPins)
            {
                var credentialText = reader.GetString(8);
                var endpointMappingText = reader.GetString(9);
                if (!Guid.TryParseExact(credentialText, "D", out var credential) || credentialText != credential.ToString() ||
                    !Guid.TryParseExact(endpointMappingText, "D", out var endpointMapping) || endpointMappingText != endpointMapping.ToString())
                    throw new InvalidOperationException("Invalid persisted target creation permit binding.");
                credentialId = credential;
                endpointMappingId = endpointMapping;
            }
            var binding = new TargetCreationPermitBinding(ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], issuedAt, startBefore)
            {
                CredentialId = credentialId,
                EndpointMappingId = endpointMappingId
            };
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
