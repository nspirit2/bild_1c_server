using System.Globalization;
using BuildSystem.Agent.Execution;
using BuildSystem.Agent.Tools;

namespace BuildSystem.Agent;

/// <summary>Append-only historical paired observations, not ownership, freshness, continuity or authority.
/// The SQL create date preserves the provider's DateTime kind; it is not a UTC observation timestamp.</summary>
public sealed class TargetRegistrationEvidence
{
    internal TargetRegistrationEvidence(TargetCreationAcknowledgement acknowledgement,
        TargetRegistrationAssessmentScope scope, SqlDatabaseObservedIdentity database)
        => (Acknowledgement, Scope, Database) = (acknowledgement, scope, database);

    public TargetCreationAcknowledgement Acknowledgement { get; }
    public TargetRegistrationAssessmentScope Scope { get; }
    public SqlDatabaseObservedIdentity Database { get; }
    public bool Executable => false;
    public bool OwnershipEstablished => false;
    public bool RestoreAllowed => false;
    public bool RetryAllowed => false;
    public bool FreshAuthority => false;
    public bool RequiresIndependentVerification => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetRegistrationEvidence [historical; not authority; details omitted]";
}

/// <summary>Fixed failure without provider messages, stored text or inner exceptions.</summary>
public sealed class TargetRegistrationEvidenceUnavailableException : Exception
{
    public TargetRegistrationEvidenceUnavailableException() : base("TargetRegistrationEvidenceUnavailable") { }
    public string Code => "TargetRegistrationEvidenceUnavailable";
    public override string ToString() => "TargetRegistrationEvidenceUnavailable";
}

public sealed partial class AgentJournal
{
    private void InitializeTargetRegistrationEvidence()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS target_registration_evidence(
                operation TEXT PRIMARY KEY REFERENCES target_creation_acknowledgements(operation) ON DELETE RESTRICT,
                attempt TEXT NOT NULL,claim TEXT NOT NULL,reported_infobase TEXT NOT NULL,
                fencing_token TEXT NOT NULL,incarnation TEXT NOT NULL,reservation TEXT NOT NULL,
                canonical_cluster TEXT NOT NULL,canonical_sql TEXT NOT NULL,
                rac_mapping TEXT NOT NULL,sql_mapping TEXT NOT NULL,
                rac_observation TEXT NOT NULL UNIQUE,sql_observation TEXT NOT NULL UNIQUE,
                profile TEXT NOT NULL,infobase_name TEXT NOT NULL,sql_server TEXT NOT NULL,
                database_name TEXT NOT NULL,database_id TEXT NOT NULL,create_date TEXT NOT NULL,state TEXT NOT NULL,
                CHECK(rac_observation<>sql_observation));
            CREATE TRIGGER IF NOT EXISTS target_registration_evidence_no_reinsert
                BEFORE INSERT ON target_registration_evidence
                WHEN NEW.rac_observation=NEW.sql_observation OR EXISTS(
                    SELECT 1 FROM target_registration_evidence WHERE operation=NEW.operation
                    OR rac_observation=NEW.rac_observation OR sql_observation=NEW.rac_observation
                    OR rac_observation=NEW.sql_observation OR sql_observation=NEW.sql_observation)
                BEGIN SELECT RAISE(ABORT,'Target registration evidence is immutable.'); END;
            CREATE TRIGGER IF NOT EXISTS target_registration_evidence_no_update
                BEFORE UPDATE ON target_registration_evidence
                BEGIN SELECT RAISE(ABORT,'Target registration evidence is immutable.'); END;
            CREATE TRIGGER IF NOT EXISTS target_registration_evidence_no_delete
                BEFORE DELETE ON target_registration_evidence
                BEGIN SELECT RAISE(ABORT,'Target registration evidence is immutable.'); END;
            """);
    }

    /// <summary>Appends once against the saved ACK in the same SQLite transaction. Exact replay fails.
    /// The expected SQL literal and the two observation mappings are caller-trusted configuration:
    /// they are not pinned by the ACK and this journal cannot independently authenticate them.
    /// No local intent transition, grant, external IO or fresh authority is produced.</summary>
    public TargetRegistrationEvidence RecordTargetRegistrationEvidence(Guid operationId,
        TargetRegistrationAssessmentResult assessment)
    {
        try
        {
            TargetRegistrationEvidence result;
            using (var transaction = _db.BeginTransaction())
            {
                ValidateTargetCreationOperation(operationId);
                if (assessment is null) throw new TargetRegistrationEvidenceUnavailableException();
                var acknowledgement = GetTargetCreationAcknowledgement(operationId)
                    ?? throw new TargetRegistrationEvidenceUnavailableException();
                ValidateRegistrationEvidence(acknowledgement, assessment.Scope, assessment.Database);
                if (assessment.InfobaseId != assessment.Scope.RegistrationScope.InfobaseId ||
                    !string.Equals(assessment.RegistrationProfileId, assessment.Scope.ExpectedProfileId, StringComparison.Ordinal))
                    throw new TargetRegistrationEvidenceUnavailableException();
                var values = RegistrationEvidenceValues(assessment.Scope, assessment.Database);
                using (var command = Command("""
                    INSERT INTO target_registration_evidence(operation,attempt,claim,reported_infobase,
                        fencing_token,incarnation,reservation,canonical_cluster,canonical_sql,rac_mapping,sql_mapping,
                        rac_observation,sql_observation,profile,infobase_name,sql_server,database_name,database_id,create_date,state)
                    VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19)
                    """, values))
                    if (command.ExecuteNonQuery() != 1) throw new TargetRegistrationEvidenceUnavailableException();
                result = new(acknowledgement, assessment.Scope, assessment.Database);
                transaction.Commit();
            }
            return result;
        }
        catch (Exception)
        {
            // Include disposal and provider failures, never propagating their potentially sensitive text.
            throw new TargetRegistrationEvidenceUnavailableException();
        }
    }

    /// <summary>Returns only inert historical metadata; absence is not proof of absence or no start.
    /// Persisted pins are checked against the strict saved ACK in one local snapshot. Ordinary SQLite
    /// immutability is not authentication against an administrator replacing the schema or database.</summary>
    public TargetRegistrationEvidence? GetTargetRegistrationEvidence(Guid operationId)
    {
        try
        {
            TargetRegistrationEvidence? result;
            using (var transaction = _db.BeginTransaction())
            {
                ValidateTargetCreationOperation(operationId);
                string[]? values = null;
                using (var command = Command("""
                    SELECT operation,attempt,claim,reported_infobase,fencing_token,incarnation,reservation,
                        canonical_cluster,canonical_sql,rac_mapping,sql_mapping,rac_observation,sql_observation,
                        profile,infobase_name,sql_server,database_name,database_id,create_date,state
                    FROM target_registration_evidence WHERE operation=$0 LIMIT 1
                    """, operationId.ToString()))
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        values = new string[20];
                        for (var i = 0; i < values.Length; i++)
                        {
                            if (reader.GetFieldType(i) != typeof(string)) throw new TargetRegistrationEvidenceUnavailableException();
                            values[i] = reader.GetString(i);
                            // Bounded after materialization; not a claim of pre-allocation protection.
                            if (values[i].Length > RegistrationEvidenceFieldLimit(i))
                                throw new TargetRegistrationEvidenceUnavailableException();
                        }
                    }
                }
                if (values is null) result = null;
                else
                {
                    var acknowledgement = GetTargetCreationAcknowledgement(operationId)
                        ?? throw new TargetRegistrationEvidenceUnavailableException();
                    var scope = new TargetRegistrationAssessmentScope(new InfobaseRegistrationReadbackScope(
                        ReadAcknowledgementId(values[0]), ReadEvidencePositiveLong(values[4]),
                        ReadAcknowledgementId(values[5]), ReadAcknowledgementId(values[6]),
                        ReadAcknowledgementId(values[7]), ReadAcknowledgementId(values[8]),
                        ReadAcknowledgementId(values[9]), ReadAcknowledgementId(values[10]),
                        ReadAcknowledgementId(values[1]), ReadAcknowledgementId(values[2]),
                        ReadAcknowledgementId(values[3]), ReadAcknowledgementId(values[11]),
                        values[14], values[15], values[16]), ReadAcknowledgementId(values[12]), values[13]);
                    var databaseId = ReadEvidencePositiveLong(values[17]);
                    if (databaseId > int.MaxValue ||
                        !DateTime.TryParseExact(values[18], "O", CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var createDate) ||
                        values[18] != createDate.ToString("O", CultureInfo.InvariantCulture))
                        throw new TargetRegistrationEvidenceUnavailableException();
                    var database = new SqlDatabaseObservedIdentity(values[16], (int)databaseId, createDate, values[19]);
                    ValidateRegistrationEvidence(acknowledgement, scope, database);
                    if (scope.RegistrationScope.OperationId != operationId ||
                        !values.SequenceEqual(RegistrationEvidenceValues(scope, database).Cast<string>(), StringComparer.Ordinal))
                        throw new TargetRegistrationEvidenceUnavailableException();
                    result = new(acknowledgement, scope, database);
                }
                transaction.Commit();
            }
            return result;
        }
        catch (Exception)
        {
            throw new TargetRegistrationEvidenceUnavailableException();
        }
    }

    private static void ValidateRegistrationEvidence(TargetCreationAcknowledgement acknowledgement,
        TargetRegistrationAssessmentScope scope, SqlDatabaseObservedIdentity database)
    {
        ValidateTargetCreationAcknowledgement(acknowledgement);
        TargetRegistrationAssessment.ValidateScope(scope);
        var pins = scope.RegistrationScope;
        var intent = acknowledgement.Intent;
        if (pins.OperationId != intent.OperationId || pins.FencingToken != intent.FencingToken ||
            pins.TargetIncarnationId != intent.TargetIncarnationId || pins.ReservationId != intent.ReservationId ||
            pins.CanonicalClusterId != intent.CanonicalClusterId || pins.CanonicalSqlServerId != intent.CanonicalSqlServerId ||
            pins.AttemptId != acknowledgement.Binding.AttemptId || pins.ClaimId != acknowledgement.ClaimId ||
            pins.InfobaseId != acknowledgement.ReportedInfobaseId ||
            !string.Equals(pins.ExpectedInfobaseName, intent.InfobaseName, StringComparison.Ordinal) ||
            !string.Equals(pins.ExpectedDatabaseName, intent.SqlDatabaseName, StringComparison.Ordinal) ||
            database is null || !string.Equals(database.Name, pins.ExpectedDatabaseName, StringComparison.Ordinal) ||
            database.DatabaseId <= 0 || database.CreateDate == default ||
            !string.Equals(database.State, "ONLINE", StringComparison.Ordinal))
            throw new TargetRegistrationEvidenceUnavailableException();
    }

    private static object[] RegistrationEvidenceValues(TargetRegistrationAssessmentScope scope, SqlDatabaseObservedIdentity database)
    {
        var pins = scope.RegistrationScope;
        return new object[] { pins.OperationId.ToString(), pins.AttemptId.ToString(), pins.ClaimId.ToString(),
            pins.InfobaseId.ToString(), pins.FencingToken.ToString(CultureInfo.InvariantCulture),
            pins.TargetIncarnationId.ToString(), pins.ReservationId.ToString(), pins.CanonicalClusterId.ToString(),
            pins.CanonicalSqlServerId.ToString(), pins.RacEndpointMappingId.ToString(), pins.SqlEndpointMappingId.ToString(),
            pins.ObservationId.ToString(), scope.SqlObservationId.ToString(), scope.ExpectedProfileId,
            pins.ExpectedInfobaseName, pins.ExpectedSqlServer, pins.ExpectedDatabaseName,
            database.DatabaseId.ToString(CultureInfo.InvariantCulture), database.CreateDate.ToString("O", CultureInfo.InvariantCulture), database.State };
    }

    private static int RegistrationEvidenceFieldLimit(int index) => index switch
    {
        4 => 19,
        13 => InfobaseRegistrationReadback.ObservedProfileId.Length,
        14 or 16 => InfobaseRegistrationReadback.MaxNameCharacters,
        15 => InfobaseRegistrationReadback.MaxSqlServerCharacters,
        17 => 10,
        18 => 33,
        19 => 6,
        _ => 36
    };

    private static long ReadEvidencePositiveLong(string text)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0 ||
            text != value.ToString(CultureInfo.InvariantCulture))
            throw new TargetRegistrationEvidenceUnavailableException();
        return value;
    }
}