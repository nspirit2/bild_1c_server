using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BuildSystem.Agent;

/// <summary>Unambiguous reported creation acknowledgement, not ownership or execution authority.
/// Independent verification remains mandatory; missing metadata is not proof that creation did not start.</summary>
public sealed record TargetCreationAcknowledgement(TargetCreationIntent Intent, TargetCreationPermitBinding Binding,
    Guid ClaimId, Guid ReportedInfobaseId, DateTimeOffset ClaimIssuedAt, DateTimeOffset ClaimExpiresAt)
{
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool RetryAllowed => false;
    public bool RestoreAllowed => false;
    public bool RequiresIndependentVerification => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool OwnershipEstablished => false;
    public override string ToString() => "TargetCreationAcknowledgement [independent verification required; details omitted]";
}

public sealed partial class AgentJournal
{
    private void InitializeTargetCreationAcknowledgements()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS target_creation_acknowledgements(
                operation TEXT PRIMARY KEY REFERENCES target_creation_intents(operation) ON DELETE RESTRICT,
                permit TEXT NOT NULL REFERENCES target_creation_permit_bindings(permit) ON DELETE RESTRICT,
                attempt TEXT NOT NULL,claim TEXT NOT NULL UNIQUE,reported_infobase TEXT NOT NULL,
                claim_issued_at TEXT NOT NULL,claim_expires_at TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS target_creation_acknowledgements_no_reinsert
                BEFORE INSERT ON target_creation_acknowledgements
                WHEN EXISTS(SELECT 1 FROM target_creation_acknowledgements WHERE operation=NEW.operation OR claim=NEW.claim)
                BEGIN SELECT RAISE(ABORT,'Target creation acknowledgements are immutable.'); END;
            CREATE TRIGGER IF NOT EXISTS target_creation_acknowledgements_no_update
                BEFORE UPDATE ON target_creation_acknowledgements
                BEGIN SELECT RAISE(ABORT,'Target creation acknowledgements are immutable.'); END;
            CREATE TRIGGER IF NOT EXISTS target_creation_acknowledgements_no_delete
                BEFORE DELETE ON target_creation_acknowledgements
                BEGIN SELECT RAISE(ABORT,'Target creation acknowledgements are immutable.'); END;
            """);
    }

    /// <summary>Only call after successful credential cleanup. Atomically appends inert acknowledgement
    /// metadata and quarantines the exact in-flight attempt. No replay, adoption, restore or retry authority.</summary>
    public TargetCreationRecord QuarantineTargetCreationWithAcknowledgement(Guid operationId, long expectedRevision,
        TargetCreationAcknowledgement acknowledgement)
    {
        ValidateTargetCreationOperation(operationId);
        ValidateTargetCreationAcknowledgement(acknowledgement);
        if (acknowledgement.Intent.OperationId != operationId || expectedRevision != 2)
            throw new ArgumentException("Invalid target creation acknowledgement correlation.");
        using var transaction = _db.BeginTransaction();
        var local = GetTargetCreationIntent(operationId)
            ?? throw new InvalidOperationException("Unknown target creation intent.");
        var binding = GetTargetCreationPermitBinding(operationId);
        if (local.State != TargetCreationState.CreationInFlight || local.Revision != expectedRevision ||
            local.AttemptExpectedRevision != 1 || local.AttemptId != acknowledgement.Binding.AttemptId ||
            local.Intent != acknowledgement.Intent || binding is null || binding != acknowledgement.Binding)
            throw new InvalidOperationException("Target creation acknowledgement state or pin conflict.");
        Execute("""
            INSERT INTO target_creation_acknowledgements(operation,permit,attempt,claim,reported_infobase,claim_issued_at,claim_expires_at)
            VALUES($0,$1,$2,$3,$4,$5,$6)
            """, operationId.ToString(), binding.PermitId.ToString(), binding.AttemptId.ToString(),
            acknowledgement.ClaimId.ToString(), acknowledgement.ReportedInfobaseId.ToString(),
            Stamp(acknowledgement.ClaimIssuedAt), Stamp(acknowledgement.ClaimExpiresAt));
        using var update = Command("""
            UPDATE target_creation_intents SET state='ReconciliationRequired',revision=3,reason='OutcomeUnknown'
            WHERE operation=$0 AND revision=$1 AND state='CreationInFlight' AND attempt=$2 AND attempt_revision=1
            """, operationId.ToString(), expectedRevision, binding.AttemptId.ToString());
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Target creation acknowledgement revision conflict.");
        transaction.Commit();
        return local with { State = TargetCreationState.ReconciliationRequired, Revision = 3, QuarantineReason = "OutcomeUnknown" };
    }

    /// <summary>Bounded inert read; absence does not prove no start and presence never establishes ownership.</summary>
    public TargetCreationAcknowledgement? GetTargetCreationAcknowledgement(Guid operationId)
    {
        ValidateTargetCreationOperation(operationId);
        try
        {
            Guid operation, permit, attempt, claim, reported;
            DateTimeOffset issuedAt, expiresAt;
            using (var command = Command("""
                SELECT operation,permit,attempt,claim,reported_infobase,claim_issued_at,claim_expires_at
                FROM target_creation_acknowledgements WHERE operation=$0 LIMIT 1
                """, operationId.ToString()))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) return null;
                operation = ReadAcknowledgementId(reader.GetString(0));
                permit = ReadAcknowledgementId(reader.GetString(1));
                attempt = ReadAcknowledgementId(reader.GetString(2));
                claim = ReadAcknowledgementId(reader.GetString(3));
                reported = ReadAcknowledgementId(reader.GetString(4));
                issuedAt = ReadAcknowledgementTime(reader.GetString(5));
                expiresAt = ReadAcknowledgementTime(reader.GetString(6));
            }
            var local = GetTargetCreationIntent(operationId);
            var binding = GetTargetCreationPermitBinding(operationId);
            if (local is null || binding is null || operation != operationId ||
                local.State != TargetCreationState.ReconciliationRequired || local.Revision != 3 ||
                local.QuarantineReason != "OutcomeUnknown" || local.AttemptExpectedRevision != 1 ||
                local.AttemptId != attempt || binding.AttemptId != attempt || binding.PermitId != permit)
                throw new InvalidOperationException("Invalid persisted target creation acknowledgement.");
            var acknowledgement = new TargetCreationAcknowledgement(local.Intent, binding, claim, reported, issuedAt, expiresAt);
            ValidateTargetCreationAcknowledgement(acknowledgement);
            return acknowledgement;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException or InvalidCastException or OverflowException or SqliteException)
        {
            // Deliberately do not retain potentially sensitive persisted text in an inner exception.
            throw new InvalidOperationException("Invalid persisted target creation acknowledgement.");
        }
    }

    private static void ValidateTargetCreationAcknowledgement(TargetCreationAcknowledgement? acknowledgement)
    {
        if (acknowledgement is null) throw new ArgumentException("Invalid target creation acknowledgement.");
        ValidateTargetCreationIntent(acknowledgement.Intent);
        var binding = acknowledgement.Binding;
        if (!ValidTargetCreationPermitBinding(binding) || binding.CredentialId is null || binding.EndpointMappingId is null ||
            binding.OperationId != acknowledgement.Intent.OperationId || acknowledgement.ClaimId == Guid.Empty ||
            acknowledgement.ReportedInfobaseId == Guid.Empty || acknowledgement.ClaimIssuedAt.Offset != TimeSpan.Zero ||
            acknowledgement.ClaimExpiresAt.Offset != TimeSpan.Zero || acknowledgement.ClaimIssuedAt < binding.IssuedAt ||
            acknowledgement.ClaimExpiresAt != binding.StartBefore || acknowledgement.ClaimIssuedAt >= acknowledgement.ClaimExpiresAt ||
            acknowledgement.ClaimExpiresAt - acknowledgement.ClaimIssuedAt > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Invalid target creation acknowledgement.");
    }

    private static Guid ReadAcknowledgementId(string text)
    {
        if (!Guid.TryParseExact(text, "D", out var id) || id == Guid.Empty || text != id.ToString())
            throw new InvalidOperationException("Invalid persisted target creation acknowledgement.");
        return id;
    }

    private static DateTimeOffset ReadAcknowledgementTime(string text)
    {
        if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
            time.Offset != TimeSpan.Zero || text != Stamp(time))
            throw new InvalidOperationException("Invalid persisted target creation acknowledgement.");
        return time;
    }
}
