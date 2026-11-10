using System.Data.Common;
using BuildSystem.Agent.Tools;

namespace BuildSystem.Agent;

/// <summary>Operator-only configuration; opt-in does not grant a claim or register a worker.</summary>
public sealed class TargetCreationTrustedAdapterOptions
{
    public bool Enabled { get; init; }
    public bool ExclusiveNamespaceConfirmed { get; init; }
    public Guid TrustedAgentId { get; init; }
    public Guid CanonicalClusterId { get; init; }
    public Guid RacClusterId { get; init; }
    public Guid CanonicalSqlServerId { get; init; }
    public Guid EndpointMappingId { get; init; }
    public Guid RacObservationMappingId { get; init; }
    public Guid SqlObservationMappingId { get; init; }
    public string TrustedRacToolId { get; init; } = string.Empty;
    public string RacEndpoint { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string SqlServer { get; init; } = string.Empty;
    public string ExpectedSqlMachineName { get; init; } = string.Empty;
    public string ExpectedSqlInstanceName { get; init; } = "MSSQLSERVER";
    public int RacTimeoutSeconds { get; init; } = 30;
    public int SqlTimeoutSeconds { get; init; } = 30;
    public int TotalObservationTimeoutSeconds { get; init; } = 60;
    public int CreationTimeoutSeconds { get; init; } = 120;
    public override string ToString() => "TargetCreationTrustedAdapterOptions [details omitted]";
}

/// <summary>Fixed failure without provider diagnostics, configuration, or an inner exception.</summary>
public sealed class TargetCreationTrustedAdapterException : Exception
{
    internal TargetCreationTrustedAdapterException() : base("TargetCreationTrustedAdapterUnavailable") { }
    public string Code => Message;
    public override string ToString() => Code;
}

/// <summary>Composed trusted ports, not authority. No CLI, worker, or automatic execution is wired.</summary>
public sealed class TargetCreationTrustedAdapterBundle
{
    internal TargetCreationTrustedAdapterBundle(ITargetCreationNamespacePort namespacePort,
        ITargetCreationAbsencePort absencePort, ITargetCreationSingleInvocation invocationPort,
        TargetCreationOneShotOptions oneShotOptions)
        => (NamespacePort, AbsencePort, InvocationPort, OneShotOptions) =
            (namespacePort, absencePort, invocationPort, oneShotOptions);

    public ITargetCreationNamespacePort NamespacePort { get; }
    public ITargetCreationAbsencePort AbsencePort { get; }
    public ITargetCreationSingleInvocation InvocationPort { get; }
    public TargetCreationOneShotOptions OneShotOptions { get; }
    public override string ToString() => "TargetCreationTrustedAdapterBundle [details omitted]";
}

/// <summary>
/// Offline composition of operator-pinned adapters. The runner and provider factory are trusted
/// operator seams, never request inputs. Construction performs no provider callbacks or target IO.
/// Enabling is not a claim: only the trusted one-shot coordinator may invoke these ports after its
/// fresh authority, journal, held-namespace and absence checks. Real seams CAN cause real creation;
/// this is not a dry run. The host must serialize access and enforce the cooperative namespace policy.
/// </summary>
public static class TargetCreationTrustedAdapters
{
    public static TargetCreationTrustedAdapterBundle Create(TargetCreationTrustedAdapterOptions options,
        IProcessRunner trustedRunner, Func<string, DbConnection> trustedProviderFactory)
    {
        // Opt-in gates precede configuration, dependency inspection, and every trusted callback.
        if (options is null || !options.Enabled || !options.ExclusiveNamespaceConfirmed)
            throw new TargetCreationTrustedAdapterException();
        try
        {
            if (options.TrustedAgentId == Guid.Empty || options.CanonicalClusterId == Guid.Empty ||
                options.RacClusterId == Guid.Empty || options.CanonicalSqlServerId == Guid.Empty ||
                options.EndpointMappingId == Guid.Empty || options.RacObservationMappingId == Guid.Empty ||
                options.SqlObservationMappingId == Guid.Empty ||
                options.RacTimeoutSeconds is < 1 or > 60 || options.SqlTimeoutSeconds is < 1 or > 60 ||
                options.TotalObservationTimeoutSeconds is < 1 or > 120 ||
                options.CreationTimeoutSeconds is < 1 or > 300 ||
                trustedRunner is null || trustedProviderFactory is null)
                throw new TargetCreationTrustedAdapterException();

            // Snapshot strings into constructors and the single fixed connection string, never options
            // into a deferred callback. Both SQL adapters share the same physical source and identity.
            var connectionString = TargetObservationHostFactory.BuildSqlConnectionString(new TargetObservationHostOptions
            {
                SqlServer = options.SqlServer, TimeoutSeconds = options.SqlTimeoutSeconds
            });
            Func<DbConnection> sqlFactory = () => trustedProviderFactory(connectionString);
            var rac = new InfobaseInventoryObserver(trustedRunner, new InfobaseInventoryObserverOptions
            {
                TrustedRacToolId = options.TrustedRacToolId, Endpoint = options.RacEndpoint,
                EndpointMappingId = options.RacObservationMappingId,
                CanonicalClusterId = options.CanonicalClusterId, RacClusterId = options.RacClusterId,
                WorkingDirectory = options.WorkingDirectory, Timeout = TimeSpan.FromSeconds(options.RacTimeoutSeconds)
            });
            var sql = new SqlDatabaseObserver(sqlFactory, new SqlDatabaseObserverOptions
            {
                EndpointMappingId = options.SqlObservationMappingId, CanonicalSqlServerId = options.CanonicalSqlServerId,
                ExpectedMachineName = options.ExpectedSqlMachineName, ExpectedInstanceName = options.ExpectedSqlInstanceName,
                TimeoutSeconds = options.SqlTimeoutSeconds
            });
            var guard = new SqlTargetNamespaceGuard(sqlFactory, new SqlTargetNamespaceGuardOptions
            {
                EndpointMappingId = options.EndpointMappingId, CanonicalSqlServerId = options.CanonicalSqlServerId,
                ExpectedMachineName = options.ExpectedSqlMachineName, ExpectedInstanceName = options.ExpectedSqlInstanceName,
                TimeoutSeconds = options.SqlTimeoutSeconds
            });
            var presence = new TargetPresenceObserver(rac, sql, new TargetPresenceObserverOptions
            {
                TotalTimeoutSeconds = options.TotalObservationTimeoutSeconds
            });
            var recipe = new RacInfobaseCreationRecipe(new RacInfobaseCreationRecipeOptions
            {
                TrustedRacToolId = options.TrustedRacToolId, CanonicalClusterId = options.CanonicalClusterId,
                RacClusterId = options.RacClusterId, CanonicalSqlServerId = options.CanonicalSqlServerId,
                EndpointMappingId = options.EndpointMappingId, Endpoint = options.RacEndpoint,
                SqlServer = options.SqlServer, WorkingDirectory = options.WorkingDirectory,
                Locale = "ru_RU", Timeout = TimeSpan.FromSeconds(options.CreationTimeoutSeconds),
                Enabled = true, ExclusiveNamespaceConfirmed = true
            });
            var invocation = new RacInfobaseCreationInvocation(trustedRunner,
                new RacInfobaseCreationInvocationOptions { Enabled = true });
            return new TargetCreationTrustedAdapterBundle(new NamespacePort(guard),
                new AbsencePort(presence, options.CanonicalClusterId, options.CanonicalSqlServerId,
                    options.RacObservationMappingId, options.SqlObservationMappingId),
                new TargetCreationDeadlineInvocationAdapter(recipe, invocation,
                    new TargetCreationDeadlineInvocationAdapterOptions { Enabled = true }),
                new TargetCreationOneShotOptions
                {
                    Enabled = true, TrustedAgentId = options.TrustedAgentId,
                    CanonicalClusterId = options.CanonicalClusterId, CanonicalSqlServerId = options.CanonicalSqlServerId,
                    EndpointMappingId = options.EndpointMappingId, RacObservationMappingId = options.RacObservationMappingId,
                    SqlObservationMappingId = options.SqlObservationMappingId
                });
        }
        catch (Exception) { throw new TargetCreationTrustedAdapterException(); }
    }

    private sealed class NamespacePort(SqlTargetNamespaceGuard guard) : ITargetCreationNamespacePort
    {
        public async Task<ITargetCreationNamespaceHold> AcquireAsync(SqlTargetNamespaceScope scope,
            CancellationToken cancellationToken = default)
        {
            try { return new NamespaceHold(await guard.AcquireAsync(scope, cancellationToken).ConfigureAwait(false)); }
            catch (Exception) { throw new TargetCreationTrustedAdapterException(); }
        }
        public override string ToString() => "TargetCreationTrustedNamespacePort [details omitted]";
    }

    private sealed class NamespaceHold(SqlTargetNamespaceLease lease) : ITargetCreationNamespaceHold
    {
        public SqlTargetNamespaceScope Scope => lease.Scope;
        public async Task CheckHeldAsync(CancellationToken cancellationToken = default)
        {
            try { await lease.CheckHeldAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) { throw new TargetCreationTrustedAdapterException(); }
        }
        public async ValueTask DisposeAsync()
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { throw new TargetCreationTrustedAdapterException(); }
        }
        public override string ToString() => "TargetCreationTrustedNamespaceHold [details omitted]";
    }

    private sealed class AbsencePort(TargetPresenceObserver observer, Guid cluster, Guid sql,
        Guid racMapping, Guid sqlMapping) : ITargetCreationAbsencePort
    {
        public async Task<TargetPresenceAssessmentResult> ObserveAsync(TargetPresenceAssessmentScope expected,
            TargetCreationDeliveredSecretValues secrets, CancellationToken cancellationToken = default)
        {
            try
            {
                // Check ALL configured pins before RAC IO, including SQL-side mismatches. Core then
                // validates every correlation/observation ID before either adapter can perform IO.
                if (expected is null || secrets is null || expected.CanonicalClusterId != cluster ||
                    expected.CanonicalSqlServerId != sql || expected.RacEndpointMappingId != racMapping ||
                    expected.SqlEndpointMappingId != sqlMapping)
                    throw new TargetCreationTrustedAdapterException();
                return await observer.ObserveExpectedScopeAsync(expected,
                    new InfobaseInventoryObserverCredentials(secrets.ClusterUser, secrets.ClusterPassword),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) { throw new TargetCreationTrustedAdapterException(); }
        }
        public override string ToString() => "TargetCreationTrustedAbsencePort [details omitted]";
    }
}
