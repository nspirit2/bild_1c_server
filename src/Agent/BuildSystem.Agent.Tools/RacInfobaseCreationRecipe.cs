namespace BuildSystem.Agent.Tools;

/// <summary>
/// Operator-owned RAC creation policy. The endpoint, both cluster identities, SQL server mapping,
/// tool ID, workspace, and timeout must come from trusted configuration, not pipeline input.
/// Enabled and ExclusiveNamespaceConfirmed default to false. Owner-approved creation locale is
/// always ru_RU; an explicit conflicting locale is rejected rather than silently overwritten.
/// </summary>
public sealed class RacInfobaseCreationRecipeOptions
{
    public required string TrustedRacToolId { get; init; }
    public required Guid CanonicalClusterId { get; init; }
    public required Guid RacClusterId { get; init; }
    public required Guid CanonicalSqlServerId { get; init; }
    public required Guid EndpointMappingId { get; init; }
    public required string Endpoint { get; init; }
    public required string SqlServer { get; init; }
    public string Locale { get; init; } = "ru_RU";
    public required string WorkingDirectory { get; init; }
    public required TimeSpan Timeout { get; init; }
    public bool Enabled { get; init; }
    public bool ExclusiveNamespaceConfirmed { get; init; }

    public override string ToString() => "RacInfobaseCreationRecipeOptions [details omitted]";
}

/// <summary>
/// Correlation pins for a single intended attempt; not a physical or executable fence.
/// An ID's presence does not certify its permit, reservation, incarnation, or freshness;
/// the authorized caller must verify all of these independently.
/// </summary>
public sealed record RacInfobaseCreationScope(
    Guid OperationId, Guid PermitId, Guid AttemptId, Guid ReservationId,
    Guid TargetIncarnationId, Guid CanonicalClusterId, Guid CanonicalSqlServerId,
    Guid EndpointMappingId)
{
    public override string ToString() => "RacInfobaseCreationScope [details omitted]";
}

/// <summary>Invocation-only credentials; never place these in diagnostics or operator options.</summary>
public sealed record RacInfobaseCreationCredentials(
    string ClusterUser, string ClusterPassword, string SqlUser, string SqlPassword)
{
    public override string ToString() => "RacInfobaseCreationCredentials [details omitted]";
}

/// <summary>Fixed-code, non-sensitive error. No input values are included in messages.</summary>
public sealed class RacInfobaseCreationRecipeException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Inert command recipe, NOT an execution permit. Even though Command contains RAC arguments,
/// executing it without a fresh, authoritative online permit/local journal/held exclusive namespace
/// and absence verification in the same critical section is forbidden. This type does not perform
/// those checks, run any process, adopt an existing target, retry creation, drop anything, or
/// register a consumer. Executable is intentionally always false.
/// </summary>
public sealed class RacInfobaseCreationCommandRecipe
{
    internal RacInfobaseCreationCommandRecipe(RacInfobaseCreationScope scope, string generatedName,
        ProcessCommand command)
    {
        Scope = scope;
        GeneratedName = generatedName;
        Command = command;
    }

    public RacInfobaseCreationScope Scope { get; }
    public string GeneratedName { get; }
    public ProcessCommand Command { get; }
    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public override string ToString() => "RacInfobaseCreationCommandRecipe [details omitted]";
}

/// <summary>
/// Constructs a single RAC infobase-create argv, without a runner, host, executor, or side effects.
/// NOT an authorization boundary: the caller must atomically verify its owner-approved permit,
/// local journal, target incarnation, namespace exclusivity, and fresh absence before execution.
/// There is no fallback, adoption, retry, drop, or post-create consumer registration here.
/// </summary>
public sealed class RacInfobaseCreationRecipe
{
    private readonly string toolId;
    private readonly Guid canonicalClusterId;
    private readonly Guid racClusterId;
    private readonly Guid canonicalSqlServerId;
    private readonly Guid endpointMappingId;
    private readonly string endpoint;
    private readonly string sqlServer;
    private readonly string locale;
    private readonly string workingDirectory;
    private readonly TimeSpan timeout;
    private readonly bool enabled;
    private readonly bool exclusiveNamespaceConfirmed;

    public RacInfobaseCreationRecipe(RacInfobaseCreationRecipeOptions options)
    {
        if (options is null || !ValidToolId(options.TrustedRacToolId) ||
            options.CanonicalClusterId == Guid.Empty || options.RacClusterId == Guid.Empty ||
            options.CanonicalSqlServerId == Guid.Empty || options.EndpointMappingId == Guid.Empty ||
            !ValidEndpoint(options.Endpoint) || !ValidSqlServer(options.SqlServer) ||
            !ValidLocale(options.Locale) ||
            string.IsNullOrWhiteSpace(options.WorkingDirectory) ||
            options.WorkingDirectory.Length > 4096 ||
            options.WorkingDirectory.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(options.WorkingDirectory) ||
            options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(5))
            throw new RacInfobaseCreationRecipeException("InvalidRacInfobaseCreationOptions");

        // Copy individual immutable values: later changes to an options instance cannot change a recipe.
        toolId = options.TrustedRacToolId;
        canonicalClusterId = options.CanonicalClusterId;
        racClusterId = options.RacClusterId;
        canonicalSqlServerId = options.CanonicalSqlServerId;
        endpointMappingId = options.EndpointMappingId;
        endpoint = options.Endpoint;
        sqlServer = options.SqlServer;
        locale = options.Locale;
        workingDirectory = options.WorkingDirectory;
        timeout = options.Timeout;
        enabled = options.Enabled;
        exclusiveNamespaceConfirmed = options.ExclusiveNamespaceConfirmed;
    }

    public RacInfobaseCreationCommandRecipe Prepare(RacInfobaseCreationScope scope,
        RacInfobaseCreationCredentials credentials)
    {
        // Check operator gates before constructing any ProcessCommand or examining invocation data.
        if (!enabled)
            throw new RacInfobaseCreationRecipeException("RacInfobaseCreationDisabled");
        if (!exclusiveNamespaceConfirmed)
            throw new RacInfobaseCreationRecipeException("RacInfobaseCreationExclusiveNamespaceRequired");
        if (scope is null || scope.OperationId == Guid.Empty || scope.PermitId == Guid.Empty ||
            scope.AttemptId == Guid.Empty || scope.ReservationId == Guid.Empty ||
            scope.TargetIncarnationId == Guid.Empty || scope.CanonicalClusterId == Guid.Empty ||
            scope.CanonicalSqlServerId == Guid.Empty || scope.EndpointMappingId == Guid.Empty)
            throw new RacInfobaseCreationRecipeException("InvalidRacInfobaseCreationScope");
        if (scope.CanonicalClusterId != canonicalClusterId ||
            scope.CanonicalSqlServerId != canonicalSqlServerId ||
            scope.EndpointMappingId != endpointMappingId)
            throw new RacInfobaseCreationRecipeException("RacInfobaseCreationScopeMismatch");
        if (credentials is null || !ValidCredential(credentials.ClusterUser) ||
            !ValidCredential(credentials.ClusterPassword) ||
            !ValidCredential(credentials.SqlUser) || !ValidCredential(credentials.SqlPassword))
            throw new RacInfobaseCreationRecipeException("InvalidRacInfobaseCreationCredentials");

        var name = "bs_test_" + scope.ReservationId.ToString("N");
        // A read-only collection prevents mutation of the fixed switches after construction.
        var arguments = Array.AsReadOnly(new[]
        {
            new ProcessArgument("infobase"),
            new ProcessArgument("create"),
            new ProcessArgument("--cluster=" + racClusterId.ToString("D")),
            new ProcessArgument("--cluster-user=" + credentials.ClusterUser, Sensitive: true),
            new ProcessArgument("--cluster-pwd=" + credentials.ClusterPassword, Sensitive: true),
            new ProcessArgument("--create-database"),
            new ProcessArgument("--name=" + name),
            new ProcessArgument("--dbms=MSSQLServer"),
            new ProcessArgument("--db-server=" + sqlServer),
            new ProcessArgument("--db-name=" + name),
            new ProcessArgument("--db-user=" + credentials.SqlUser, Sensitive: true),
            new ProcessArgument("--db-pwd=" + credentials.SqlPassword, Sensitive: true),
            new ProcessArgument("--locale=" + locale),
            new ProcessArgument("--scheduled-jobs-deny=on"),
            new ProcessArgument(endpoint)
        });
        var secrets = Array.AsReadOnly(new[]
        {
            credentials.ClusterUser, credentials.ClusterPassword, credentials.SqlUser, credentials.SqlPassword
        });
        var command = new ProcessCommand(toolId, workingDirectory, arguments, timeout, secrets);
        return new RacInfobaseCreationCommandRecipe(scope, name, command);
    }

    private static bool ValidToolId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value[0] != '-' &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    // Same endpoint constraints as InfobaseInventoryObserver: no whitespace, controls, or leading '-'.
    private static bool ValidEndpoint(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 255 && value[0] != '-' &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    // SQL instance address, never SQL text or a shell expression. RAC receives this as one argument.
    private static bool ValidSqlServer(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 255 && value[0] != '-' &&
        value.All(c => (c is >= 'a' and <= 'z') || (c is >= 'A' and <= 'Z') ||
                       (c is >= '0' and <= '9') || c is '.' or '_' or '-' or '\\' or ',' or ':');

    private static bool ValidLocale(string? value) =>
        string.Equals(value, "ru_RU", StringComparison.Ordinal);

    private static bool ValidCredential(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 4096 && !value.Any(char.IsControl);
}
