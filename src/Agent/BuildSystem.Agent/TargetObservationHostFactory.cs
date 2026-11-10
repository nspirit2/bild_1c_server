using BuildSystem.Agent.Tools;
using Microsoft.Data.SqlClient;

namespace BuildSystem.Agent;

/// <summary>Composes only operator-pinned, read-only observers; construction never loads credentials or contacts targets.</summary>
public static class TargetObservationHostFactory
{
    private const string InvalidConfiguration = "Invalid target observation host configuration.";

    public static ITargetObservationExecutor CreateExecutor(TargetObservationHostOptions options, string dataDirectory)
    {
        try
        {
            if (options is null || !options.Enabled || options.AgentId == Guid.Empty ||
                options.CanonicalClusterId == Guid.Empty || options.RacClusterId == Guid.Empty || options.CanonicalSqlServerId == Guid.Empty ||
                options.RacEndpointMappingId == Guid.Empty || options.SqlEndpointMappingId == Guid.Empty ||
                string.IsNullOrWhiteSpace(options.RacExecutable) ||
                !Path.IsPathFullyQualified(options.RacExecutable) || options.RacExecutable.Any(char.IsControl) ||
                !string.Equals(Path.GetFileName(options.RacExecutable), "rac.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException();

            var runner = new ProcessRunner(new ProcessRunnerOptions
            {
                TrustedTools = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["observation-rac"] = options.RacExecutable
                },
                AllowedWorkspaceRoots = [options.RacWorkingDirectory],
                InheritedEnvironmentVariables = ["SystemRoot", "WINDIR", "TEMP", "TMP"]
            });
            var rac = new InfobaseInventoryObserver(runner, new InfobaseInventoryObserverOptions
            {
                TrustedRacToolId = "observation-rac",
                Endpoint = options.RacEndpoint,
                EndpointMappingId = options.RacEndpointMappingId,
                CanonicalClusterId = options.CanonicalClusterId,
                RacClusterId = options.RacClusterId,
                WorkingDirectory = options.RacWorkingDirectory,
                Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
            });
            var connectionString = BuildSqlConnectionString(options);
            var sql = new SqlDatabaseObserver(() => new SqlConnection(connectionString), new SqlDatabaseObserverOptions
            {
                EndpointMappingId = options.SqlEndpointMappingId,
                CanonicalSqlServerId = options.CanonicalSqlServerId,
                ExpectedMachineName = options.ExpectedSqlMachineName,
                ExpectedInstanceName = options.ExpectedSqlInstanceName,
                TimeoutSeconds = options.TimeoutSeconds
            });
            var paired = new TargetPresenceObserver(rac, sql, new TargetPresenceObserverOptions
            {
                TotalTimeoutSeconds = options.TotalTimeoutSeconds
            });
            var executor = new TargetObservationExecutor(options.AgentId, paired,
                () => ObservationCredentialStore.Load(dataDirectory));
            // This boundary must remain outermost: all five local pins precede credential and target IO.
            return new TargetObservationPinnedExecutor(options, executor);
        }
        catch (Exception)
        {
            // Public init-only options can bypass Bind; never expose constructor/provider diagnostics.
            throw new InvalidOperationException(InvalidConfiguration);
        }
    }

    /// <summary>Builds a fixed integrated-security master connection, never an operator connection-string fragment.</summary>
    public static string BuildSqlConnectionString(TargetObservationHostOptions options)
    {
        try
        {
            if (options is null || string.IsNullOrWhiteSpace(options.SqlServer) ||
                options.SqlServer.Length > 255 ||
                options.SqlServer.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ';' or '=') ||
                options.TimeoutSeconds is < 1 or > 60)
                throw new InvalidOperationException();
            return new SqlConnectionStringBuilder
            {
                DataSource = options.SqlServer,
                IntegratedSecurity = true,
                InitialCatalog = "master",
                Encrypt = SqlConnectionEncryptOption.Mandatory,
                TrustServerCertificate = false,
                ConnectTimeout = options.TimeoutSeconds,
                Pooling = false,
                ConnectRetryCount = 0,
                ApplicationName = "BuildSystem.TargetObservation"
            }.ConnectionString;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(InvalidConfiguration);
        }
    }
}
