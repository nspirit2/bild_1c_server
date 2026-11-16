using System.Globalization;

namespace BuildSystem.Agent;

public sealed partial class AgentJournal
{
    private void InitializeTargetRegistrationEvidenceDelivery()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS target_registration_evidence_delivery_acknowledgements(
                operation TEXT PRIMARY KEY REFERENCES target_registration_evidence(operation) ON DELETE RESTRICT,
                claim TEXT NOT NULL UNIQUE,
                accepted_at TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS target_registration_evidence_delivery_acknowledgements_no_reinsert
                BEFORE INSERT ON target_registration_evidence_delivery_acknowledgements
                WHEN EXISTS (SELECT 1 FROM target_registration_evidence_delivery_acknowledgements
                    WHERE operation=NEW.operation OR claim=NEW.claim)
                BEGIN SELECT RAISE(ABORT,'Target registration evidence delivery is immutable.'); END;
            CREATE TRIGGER IF NOT EXISTS target_registration_evidence_delivery_acknowledgements_no_update
                BEFORE UPDATE ON target_registration_evidence_delivery_acknowledgements
                BEGIN SELECT RAISE(ABORT,'Target registration evidence delivery is immutable.'); END;
            CREATE TRIGGER IF NOT EXISTS target_registration_evidence_delivery_acknowledgements_no_delete
                BEFORE DELETE ON target_registration_evidence_delivery_acknowledgements
                BEGIN SELECT RAISE(ABORT,'Target registration evidence delivery is immutable.'); END;
            """);
    }

    /// <summary>Projects only the strict saved historical evidence; not ownership, freshness, or authority.</summary>
    public TargetRegistrationEvidenceReport PrepareTargetRegistrationEvidenceReport(Guid operationId)
    {
        try
        {
            TargetRegistrationEvidenceReport result;
            using (var transaction = _db.BeginTransaction())
            {
                var evidence = ReadTargetRegistrationEvidenceUnderCurrentTransaction(operationId)
                    ?? throw new TargetRegistrationEvidenceDeliveryUnavailableException();
                result = ProjectTargetRegistrationEvidenceReport(evidence);
                transaction.Commit();
            }
            return result;
        }
        catch (Exception)
        {
            throw new TargetRegistrationEvidenceDeliveryUnavailableException();
        }
    }

    /// <summary>Reads only a strict locally persisted delivery acknowledgement for this explicit operation.</summary>
    public TargetRegistrationEvidenceDeliveryAcknowledgement? GetTargetRegistrationEvidenceDeliveryAcknowledgement(Guid operationId)
    {
        try
        {
            TargetRegistrationEvidenceDeliveryAcknowledgement? result;
            using (var transaction = _db.BeginTransaction())
            {
                result = ReadTargetRegistrationEvidenceDeliveryAcknowledgementUnderCurrentTransaction(operationId);
                transaction.Commit();
            }
            return result;
        }
        catch (Exception)
        {
            throw new TargetRegistrationEvidenceDeliveryUnavailableException();
        }
    }

    /// <summary>Appends an inert historical delivery ACK only if it matches the actual strict saved evidence.
    /// Exact canonical replay reads the existing row without another write; conflicting replay fails closed.</summary>
    public TargetRegistrationEvidenceDeliveryAcknowledgement RecordTargetRegistrationEvidenceDeliveryAcknowledgement(
        Guid operationId, TargetRegistrationEvidenceReport expectedReport, TargetRegistrationEvidenceDeliveryReceipt receipt)
    {
        try
        {
            TargetRegistrationEvidenceDeliveryAcknowledgement result;
            using (var transaction = _db.BeginTransaction())
            {
                var evidence = ReadTargetRegistrationEvidenceUnderCurrentTransaction(operationId)
                    ?? throw new TargetRegistrationEvidenceDeliveryUnavailableException();
                var actualReport = ProjectTargetRegistrationEvidenceReport(evidence);
                if (expectedReport is null || actualReport != expectedReport || receipt is null ||
                    receipt.OperationId != operationId || receipt.ClaimId != evidence.Acknowledgement.ClaimId ||
                    !string.Equals(receipt.Status, "Recorded", StringComparison.Ordinal) ||
                    receipt.AcceptedAt == default || receipt.AcceptedAt.Offset != TimeSpan.Zero)
                    throw new TargetRegistrationEvidenceDeliveryUnavailableException();
                var acceptedAt = receipt.AcceptedAt.ToString("O", CultureInfo.InvariantCulture);
                if (acceptedAt.Length != 33)
                    throw new TargetRegistrationEvidenceDeliveryUnavailableException();
                var existing = ReadTargetRegistrationEvidenceDeliveryAcknowledgementUnderCurrentTransaction(operationId);
                if (existing is not null)
                {
                    if (existing.Report != actualReport || existing.Receipt != receipt)
                        throw new TargetRegistrationEvidenceDeliveryUnavailableException();
                    result = existing;
                }
                else
                {
                    using (var command = Command("""
                        INSERT INTO target_registration_evidence_delivery_acknowledgements(operation,claim,accepted_at)
                        VALUES($0,$1,$2)
                        """, operationId.ToString(), actualReport.ClaimId.ToString(), acceptedAt))
                        if (command.ExecuteNonQuery() != 1)
                            throw new TargetRegistrationEvidenceDeliveryUnavailableException();
                    result = ReadTargetRegistrationEvidenceDeliveryAcknowledgementUnderCurrentTransaction(operationId)
                        ?? throw new TargetRegistrationEvidenceDeliveryUnavailableException();
                    if (result.Report != actualReport || result.Receipt != receipt)
                        throw new TargetRegistrationEvidenceDeliveryUnavailableException();
                }
                transaction.Commit();
            }
            return result;
        }
        catch (Exception)
        {
            throw new TargetRegistrationEvidenceDeliveryUnavailableException();
        }
    }

    private TargetRegistrationEvidenceDeliveryAcknowledgement? ReadTargetRegistrationEvidenceDeliveryAcknowledgementUnderCurrentTransaction(Guid operationId)
    {
        var evidence = ReadTargetRegistrationEvidenceUnderCurrentTransaction(operationId);
        string? storedOperation = null, storedClaim = null, storedAcceptedAt = null;
        using (var command = Command("""
            SELECT operation,claim,accepted_at FROM target_registration_evidence_delivery_acknowledgements
            WHERE operation=$0 LIMIT 1
            """, operationId.ToString()))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetFieldType(0) != typeof(string) || reader.GetFieldType(1) != typeof(string) ||
                    reader.GetFieldType(2) != typeof(string))
                    throw new TargetRegistrationEvidenceDeliveryUnavailableException();
                storedOperation = reader.GetString(0);
                storedClaim = reader.GetString(1);
                storedAcceptedAt = reader.GetString(2);
                if (storedOperation.Length != 36 || storedClaim.Length != 36 || storedAcceptedAt.Length != 33)
                    throw new TargetRegistrationEvidenceDeliveryUnavailableException();
            }
        }
        if (storedOperation is null) return null;
        if (evidence is null || storedOperation != operationId.ToString() ||
            !Guid.TryParseExact(storedClaim, "D", out var claimId) || claimId == Guid.Empty ||
            storedClaim != claimId.ToString() || claimId != evidence.Acknowledgement.ClaimId ||
            !DateTimeOffset.TryParseExact(storedAcceptedAt, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var acceptedAt) || acceptedAt == default ||
            acceptedAt.Offset != TimeSpan.Zero ||
            storedAcceptedAt != acceptedAt.ToString("O", CultureInfo.InvariantCulture))
            throw new TargetRegistrationEvidenceDeliveryUnavailableException();
        var report = ProjectTargetRegistrationEvidenceReport(evidence);
        var receipt = new TargetRegistrationEvidenceDeliveryReceipt(claimId, operationId, "Recorded", acceptedAt);
        return new TargetRegistrationEvidenceDeliveryAcknowledgement(operationId, report, receipt);
    }

    private static TargetRegistrationEvidenceReport ProjectTargetRegistrationEvidenceReport(TargetRegistrationEvidence evidence)
    {
        var registration = evidence.Scope.RegistrationScope;
        var database = evidence.Database;
        return new TargetRegistrationEvidenceReport(evidence.Acknowledgement.ClaimId,
            evidence.Acknowledgement.ReportedInfobaseId, registration.ObservationId,
            evidence.Scope.SqlObservationId, registration.RacEndpointMappingId,
            registration.SqlEndpointMappingId, evidence.Scope.ExpectedProfileId,
            registration.ExpectedInfobaseName, registration.ExpectedSqlServer,
            registration.ExpectedDatabaseName, database.DatabaseId,
            database.CreateDate.ToString("O", CultureInfo.InvariantCulture), database.State);
    }
}
