namespace BuildSystem.Agent.Execution;

/// <summary>
/// Caller correlation only. CanonicalClusterId is a canonical application identity, NOT the physical
/// RAC cluster UUID; that physical UUID belongs in separately trusted observer options. InfobaseId
/// must come from the saved create ACK. ObservationId supplies no freshness or replay protection.
/// </summary>
public sealed record InfobaseRegistrationReadbackScope(Guid OperationId, long FencingToken,
    Guid TargetIncarnationId, Guid ReservationId, Guid CanonicalClusterId, Guid CanonicalSqlServerId,
    Guid RacEndpointMappingId, Guid SqlEndpointMappingId, Guid AttemptId, Guid ClaimId,
    Guid InfobaseId, Guid ObservationId, string ExpectedInfobaseName, string ExpectedSqlServer,
    string ExpectedDatabaseName)
{
    public override string ToString() => "InfobaseRegistrationReadbackScope [details omitted]";
}

public sealed class InfobaseRegistrationReadbackException : Exception
{
    internal InfobaseRegistrationReadbackException(string code) : base(code) => Code = code;
    public string Code { get; }
}

/// <summary>
/// Immutable contract-profile observation, never authority, ownership, readiness or permission.
/// Construction is parser-only. No authentic transport, physical SQL server identity, lock,
/// elapsed-time freshness, service epoch, independent SQL evidence or replay prevention is proved.
/// </summary>
public sealed class InfobaseRegistrationReadbackResult
{
    internal InfobaseRegistrationReadbackResult(InfobaseRegistrationReadbackScope scope,
        Guid infobaseId, string infobaseName, string sqlServer, string databaseName, string dbms,
        string profileId)
    {
        ProfileId = profileId;
        Scope = scope;
        InfobaseId = infobaseId;
        InfobaseName = infobaseName;
        SqlServer = sqlServer;
        DatabaseName = databaseName;
        Dbms = dbms;
    }

    public InfobaseRegistrationReadbackScope Scope { get; }
    public string ProfileId { get; }
    public Guid InfobaseId { get; }
    public string InfobaseName { get; }
    public string SqlServer { get; }
    public string DatabaseName { get; }
    public string Dbms { get; }
    public bool ScheduledJobsBlocked => true;
    public bool Executable => false;
    public bool OwnershipEstablished => false;
    public bool RestoreAllowed => false;
    public bool RetryAllowed => false;
    public bool RequiresIndependentVerification => true;
    public bool FreshAuthority => false;
    public override string ToString() => "InfobaseRegistrationReadbackResult [contract only; not authority; details omitted]";
}

/// <summary>
/// The default is the unchanged synthetic, closed six-field CONTRACT profile. The explicitly
/// selected observed profile accepts the 21-key shape of an authorized read-only RAC 8.3.22.2557
/// sample (exit zero, no stderr, 1219 characters, two terminal blank lines). Its extras were redacted
/// on Windows before transfer: neither byte-exact whitespace nor raw secret values were preserved.
/// Tests reconstruct that shape; they are NOT a byte-exact real-output fixture. The sampled bs_smoke
/// target is not a reservation target and cannot be adopted. Output does not attest the binary
/// version: operator-owned trusted executable/version assertion is separate, not independent binary
/// attestation. There is no IO here.
/// A trusted future adapter must bind the physical cluster, endpoint, command and normal complete
/// capture; exit zero alone does not authenticate them. SQL server comparison is literal Ordinal,
/// not case folding, alias resolution, normalization or proof of physical SQL identity.
/// </summary>
public static class InfobaseRegistrationReadback
{
    public const string ProfileId = "rac-registration-contract-v1";
    public const string ObservedProfileId = "rac-registration-8.3.22.2557-v1";
    public const int ObservedMaxFields = 21;
    public const int MaxExtraValueCharacters = 4096;
    public const int MaxOutputCharacters = 16384;
    public const int MaxFields = 6;
    public const int MaxNameCharacters = 128;
    public const int MaxSqlServerCharacters = 255;

    public static InfobaseRegistrationReadbackResult Parse(InfobaseRegistrationReadbackScope scope,
        int exitCode, bool outputTruncated, string stdout) =>
        Parse(scope, exitCode, outputTruncated, stdout, ProfileId);

    public static InfobaseRegistrationReadbackResult Parse(InfobaseRegistrationReadbackScope scope,
        int exitCode, bool outputTruncated, string stdout, string profileId)
    {
        ValidateProfile(profileId);
        var observed = profileId == ObservedProfileId;
        Validate(scope);
        if (exitCode != 0 || outputTruncated) throw Error("InfobaseRegistrationUnavailable");
        if (stdout is null || stdout.Length > MaxOutputCharacters ||
            stdout.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw Error("InfobaseRegistrationInvalidReadback");
        var normalized = stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains('\r')) throw Error("InfobaseRegistrationInvalidReadback");
        // Only the six required values are retained. Seen keys (including potentially secret
        // extra fields) detect duplicates without retaining extra values. Substrings can exist
        // transiently in managed memory; this is data minimization, not forensic erasure.
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var trailingBlank = false;
        var lines = normalized.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            if (observed && raw.Trim(' ', '\t').Length == 0)
            {
                if (keys.Count == 0) throw Error("InfobaseRegistrationInvalidReadback");
                trailingBlank = true;
                continue;
            }
            // Keep the synthetic profile's single optional terminal line ending unchanged.
            if (!observed && index == lines.Length - 1 && raw.Length == 0) continue;
            if (trailingBlank) throw Error("InfobaseRegistrationInvalidReadback");
            var colon = raw.IndexOf(':');
            if (colon < 1) throw Error("InfobaseRegistrationInvalidReadback");
            var key = raw[..colon].Trim(' ', '\t');
            var required = key is "infobase" or "name" or "dbms" or "db-server" or "db-name" or "scheduled-jobs-deny";
            if ((!required && (!observed || !IsObservedExtra(key))) ||
                keys.Count >= (observed ? ObservedMaxFields : MaxFields) || !keys.Add(key))
                throw Error("InfobaseRegistrationInvalidReadback");
            var value = raw[(colon + 1)..].Trim(' ', '\t');
            if (value.Any(char.IsControl) || (required ? value.Length == 0 : value.Length > MaxExtraValueCharacters))
                throw Error("InfobaseRegistrationInvalidReadback");
            if (required) fields.Add(key, value);
        }
        if (keys.Count != (observed ? ObservedMaxFields : MaxFields) || fields.Count != MaxFields ||
            !Guid.TryParseExact(fields["infobase"], "D", out var id) || id == Guid.Empty ||
            !string.Equals(fields["infobase"], id.ToString("D"), StringComparison.Ordinal) ||
            fields["name"].Length > MaxNameCharacters || fields["db-name"].Length > MaxNameCharacters ||
            !ValidServer(fields["db-server"]))
            throw Error("InfobaseRegistrationInvalidReadback");
        if (id != scope.InfobaseId ||
            !string.Equals(fields["name"], scope.ExpectedInfobaseName, StringComparison.Ordinal) ||
            !string.Equals(fields["db-server"], scope.ExpectedSqlServer, StringComparison.Ordinal) ||
            !string.Equals(fields["db-name"], scope.ExpectedDatabaseName, StringComparison.Ordinal) ||
            !string.Equals(fields["dbms"], "MSSQLServer", StringComparison.Ordinal))
            throw Error("InfobaseRegistrationTargetMismatch");
        if (!string.Equals(fields["scheduled-jobs-deny"], "on", StringComparison.Ordinal))
            throw Error("InfobaseRegistrationJobsNotBlocked");
        return new(scope, id, fields["name"], fields["db-server"], fields["db-name"], fields["dbms"], profileId);
    }

    private static bool IsObservedExtra(string key) => key is
        "db-user" or "security-level" or "license-distribution" or "sessions-deny" or
        "denied-from" or "denied-message" or "denied-parameter" or "denied-to" or
        "permission-code" or "external-session-manager-connection-string" or
        "external-session-manager-required" or "security-profile-name" or
        "safe-mode-security-profile-name" or "reserve-working-processes" or "descr";

    private static void ValidateProfile(string profileId)
    {
        if (profileId is not (ProfileId or ObservedProfileId))
            throw Error("InfobaseRegistrationInvalidProfile");
    }

    /// <summary>
    /// Exact captured correlation only; does not bind the profile. Same-scope replay is not prevented.
    /// Use the explicit-profile overload when a profile binding is required.
    /// </summary>
    public static void RequireMatch(InfobaseRegistrationReadbackResult result,
        InfobaseRegistrationReadbackScope scope)
    {
        Validate(scope);
        if (result is null || result.Scope != scope) throw Error("InfobaseRegistrationScopeMismatch");
    }

    /// <summary>Exact scope and selected profile binding; not freshness or authority.</summary>
    public static void RequireMatch(InfobaseRegistrationReadbackResult result,
        InfobaseRegistrationReadbackScope scope, string expectedProfileId)
    {
        ValidateProfile(expectedProfileId);
        RequireMatch(result, scope);
        if (!string.Equals(result.ProfileId, expectedProfileId, StringComparison.Ordinal))
            throw Error("InfobaseRegistrationProfileMismatch");
    }

    private static void Validate(InfobaseRegistrationReadbackScope scope)
    {
        if (scope is null || scope.FencingToken <= 0 ||
            new[] { scope.OperationId, scope.TargetIncarnationId, scope.ReservationId,
                scope.CanonicalClusterId, scope.CanonicalSqlServerId, scope.RacEndpointMappingId,
                scope.SqlEndpointMappingId, scope.AttemptId, scope.ClaimId, scope.InfobaseId,
                scope.ObservationId }.Any(id => id == Guid.Empty) ||
            !string.Equals(scope.ExpectedInfobaseName, $"bs_test_{scope.ReservationId:N}", StringComparison.Ordinal) ||
            !string.Equals(scope.ExpectedDatabaseName, $"bs_test_{scope.ReservationId:N}", StringComparison.Ordinal) ||
            !ValidServer(scope.ExpectedSqlServer))
            throw Error("InfobaseRegistrationInvalidScope");
    }

    // A bounded trusted literal, not a parser for SQL host/instance/port aliases. ASCII only;
    // whitespace, controls and leading '-' are excluded rather than normalized.
    private static bool ValidServer(string value) => value is { Length: > 0 and <= MaxSqlServerCharacters } &&
        value[0] != '-' && value.All(c => c is >= '!' and <= '~');

    private static InfobaseRegistrationReadbackException Error(string code) => new(code);
}
