using BuildSystem.Agent.Execution;

namespace BuildSystem.Agent.Tools;

/// <summary>Saved ACK correlation plus two distinct observation IDs; never authority or freshness.</summary>
public sealed record TargetRegistrationAssessmentScope(InfobaseRegistrationReadbackScope RegistrationScope,
    Guid SqlObservationId, string ExpectedProfileId)
{
    public override string ToString() => "TargetRegistrationAssessmentScope [details omitted]";
}

/// <summary>
/// Point-in-time paired evidence only, not readiness, ownership, continuity or permission. No prior
/// fingerprint baseline is available: the SQL snapshot cannot prove database incarnation continuity.
/// </summary>
public sealed class TargetRegistrationAssessmentResult
{
    internal TargetRegistrationAssessmentResult(TargetRegistrationAssessmentScope scope,
        SqlDatabaseObservedIdentity database, Guid infobaseId, string registrationProfileId)
        => (Scope, Database, InfobaseId, RegistrationProfileId) = (scope, database, infobaseId, registrationProfileId);

    public TargetRegistrationAssessmentScope Scope { get; }
    public SqlDatabaseObservedIdentity Database { get; }
    public Guid InfobaseId { get; }
    public string RegistrationProfileId { get; }
    public bool Executable => false;
    public bool OwnershipEstablished => false;
    public bool RestoreAllowed => false;
    public bool RetryAllowed => false;
    public bool FreshAuthority => false;
    public bool RequiresIndependentVerification => true;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "TargetRegistrationAssessmentResult [not authority; details omitted]";
}

/// <summary>Fixed redacted failure without provider, target or endpoint details.</summary>
public sealed class TargetRegistrationAssessmentException : Exception
{
    internal TargetRegistrationAssessmentException(string code) : base(code) => Code = code;
    public string Code { get; }
    public override string ToString() => $"TargetRegistrationAssessmentException: {Code}";
}

/// <summary>
/// Pure, inert composition of explicit observed-profile RAC registration and independent SQL readback.
/// The canonical SQL identity/mapping trusts the observer's operator assertion, not a physical SQL
/// GUID discovered here. No DNS, alias inference, IO, locking, persistence or freshness check occurs.
/// ONLINE is a conservative observation requirement, not a declaration that the target is ready.
/// Same-scope replay and changes between/after the observations remain undetectable.
/// </summary>
public static class TargetRegistrationAssessment
{
    public static TargetRegistrationAssessmentResult Assess(TargetRegistrationAssessmentScope expected,
        InfobaseRegistrationReadbackResult rac, SqlDatabaseObservationResult sql)
    {
        ValidateScope(expected);
        var registration = expected.RegistrationScope;

        if (rac is null || sql is null) throw Error("TargetRegistrationUnavailable");
        try { InfobaseRegistrationReadback.RequireMatch(rac, registration, expected.ExpectedProfileId); }
        catch (InfobaseRegistrationReadbackException) { throw Error("TargetRegistrationScopeMismatch"); }
        var sqlScope = new SqlDatabaseObservationScope(registration.OperationId, registration.FencingToken,
            registration.TargetIncarnationId, registration.CanonicalSqlServerId, registration.SqlEndpointMappingId,
            registration.ReservationId, expected.SqlObservationId, registration.ExpectedDatabaseName);
        if (sql.Scope != sqlScope) throw Error("TargetRegistrationScopeMismatch");
        // Result construction is parser-only, but keep the cross-evidence invariant explicit.
        if (rac.InfobaseId != registration.InfobaseId ||
            !string.Equals(rac.InfobaseName, registration.ExpectedInfobaseName, StringComparison.Ordinal) ||
            !string.Equals(rac.SqlServer, registration.ExpectedSqlServer, StringComparison.Ordinal) ||
            !string.Equals(rac.DatabaseName, registration.ExpectedDatabaseName, StringComparison.Ordinal) ||
            !string.Equals(rac.Dbms, "MSSQLServer", StringComparison.Ordinal) || !rac.ScheduledJobsBlocked)
            throw Error("TargetRegistrationScopeMismatch");
        if (sql.Observation != SqlDatabaseObservation.Present || sql.Database is null)
            throw Error("TargetRegistrationUnavailable");
        var database = sql.Database;
        if (!string.Equals(database.Name, registration.ExpectedDatabaseName, StringComparison.Ordinal) ||
            database.DatabaseId <= 0 || database.CreateDate == default)
            throw Error("TargetRegistrationSqlTargetMismatch");
        if (!string.Equals(database.State, "ONLINE", StringComparison.Ordinal))
            throw Error("TargetRegistrationSqlNotOnline");
        return new(expected, database, rac.InfobaseId, rac.ProfileId);
    }

    /// <summary>Validates all correlation pins before any adapter IO; confers no authority.</summary>
    public static void ValidateScope(TargetRegistrationAssessmentScope expected)
    {
        if (expected is null || expected.RegistrationScope is null ||
            expected.SqlObservationId == Guid.Empty ||
            expected.SqlObservationId == expected.RegistrationScope.ObservationId ||
            !string.Equals(expected.ExpectedProfileId, InfobaseRegistrationReadback.ObservedProfileId, StringComparison.Ordinal))
            throw Error("TargetRegistrationInvalidScope");
        var scope = expected.RegistrationScope;
        if (scope.FencingToken <= 0 ||
            new[] { scope.OperationId, scope.TargetIncarnationId, scope.ReservationId,
                scope.CanonicalClusterId, scope.CanonicalSqlServerId, scope.RacEndpointMappingId,
                scope.SqlEndpointMappingId, scope.AttemptId, scope.ClaimId, scope.InfobaseId,
                scope.ObservationId }.Any(id => id == Guid.Empty) ||
            !string.Equals(scope.ExpectedInfobaseName, $"bs_test_{scope.ReservationId:N}", StringComparison.Ordinal) ||
            !string.Equals(scope.ExpectedDatabaseName, $"bs_test_{scope.ReservationId:N}", StringComparison.Ordinal) ||
            scope.ExpectedSqlServer is not { Length: > 0 and <= InfobaseRegistrationReadback.MaxSqlServerCharacters } ||
            scope.ExpectedSqlServer[0] == '-' || !scope.ExpectedSqlServer.All(c => c is >= '!' and <= '~'))
            throw Error("TargetRegistrationInvalidScope");
    }

    private static TargetRegistrationAssessmentException Error(string code) => new(code);
}
