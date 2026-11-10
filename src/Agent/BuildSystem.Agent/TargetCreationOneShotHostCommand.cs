using BuildSystem.Agent.Tools;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace BuildSystem.Agent;

/// <summary>
/// Dormant operator-only real composition; deliberately not registered by Program or a worker.
/// Owns the exclusive journal and all transports until cleanup completes, before one output attempt.
/// An acknowledgement is metadata, never success or permission to release scheduled jobs.
/// </summary>
public static class TargetCreationOneShotHostCommand
{
    public static async Task<int> RunAsync(Guid permitId, IConfiguration configuration, TextWriter output,
        CancellationToken ct = default)
    {
        int code;
        string? failure = null;
        var commandCompleted = false;
        using var buffer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            // Default-off policy precedes cancellation, OS checks, credentials, and dependencies.
            var options = TargetCreationOneShotHostOptions.Bind(configuration);
            if (!options.Enabled) return await Emit(output, "TargetCreationOneShotDisabled", 3);
            ct.ThrowIfCancellationRequested();
            if (permitId == Guid.Empty || !OperatingSystem.IsWindows()) throw new InvalidOperationException();
            var directory = configuration["Agent:DataDirectory"];
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
                directory.Any(char.IsControl) || !Directory.Exists(directory) ||
                !File.Exists(Path.Combine(directory, "agent-journal.sqlite"))) throw new InvalidOperationException();

            // This owner serializes the entire invocation, including local preflight and cleanup.
            using var journal = new AgentJournal(directory);
            var local = journal.GetTargetCreationIntent(options.OperationId) ?? throw new InvalidOperationException();
            var binding = journal.GetTargetCreationPermitBinding(options.OperationId) ?? throw new InvalidOperationException();
            if (local.Intent.OperationId != options.OperationId || binding.OperationId != options.OperationId ||
                binding.PermitId != permitId || binding.AgentId != options.AgentId ||
                local.Intent.CanonicalClusterId != options.CanonicalClusterId ||
                local.Intent.CanonicalSqlServerId != options.CanonicalSqlServerId ||
                binding.CredentialId != options.CredentialId || binding.EndpointMappingId != options.EndpointMappingId)
                throw new InvalidOperationException();

            if (local.State != TargetCreationState.IntentRecorded)
            {
                // No endpoint/client/target factory construction, GET, or lifecycle entry on replay.
                code = await Emit(buffer, "TargetCreationOneShotAlreadyAttempted", 2);
            }
            else
            {
                _ = TargetCreationLocalPreflight.ValidateInitial(local, binding);
                if (!Uri.TryCreate(configuration["Agent:ApiOrigin"], UriKind.Absolute, out var origin))
                    throw new InvalidOperationException();
                Func<string> bearer = () => TokenStore.Load(Path.Combine(directory, "agent-token.dpapi"));
                // Client constructors validate the HTTPS origin without loading a token or doing IO.
                using var permits = new TargetCreationPermitHttpClient(new()
                { Enabled = true, BaseUri = origin, TimeoutSeconds = options.HttpTimeoutSeconds }, bearer);
                using var credentials = new TargetCreationCredentialHttpClient(new()
                { Enabled = true, BaseUri = origin, TimeoutSeconds = options.HttpTimeoutSeconds }, bearer);
                using var claims = new TargetCreationExecutionClaimHttpClient(new()
                { Enabled = true, BaseUri = origin, TimeoutSeconds = options.HttpTimeoutSeconds }, bearer);
                var runner = new ProcessRunner(new ProcessRunnerOptions
                {
                    TrustedTools = new Dictionary<string, string>(StringComparer.Ordinal)
                    { ["target-creation-rac"] = options.RacExecutable },
                    AllowedWorkspaceRoots = [options.RacWorkingDirectory],
                    InheritedEnvironmentVariables = ["SystemRoot", "WINDIR", "TEMP", "TMP"],
                    MaxOutputCharacters = 65536
                });
                var bundle = TargetCreationTrustedAdapters.Create(options.ToAdapterOptions(), runner,
                    connectionString => new SqlConnection(connectionString));
                var preparation = new TargetCreationCredentialPreparation(journal, permits, credentials,
                    new() { Enabled = true });
                var coordinator = new TargetCreationOneShotCoordinator(journal, preparation, bundle.NamespacePort,
                    bundle.AbsencePort, claims, bundle.InvocationPort, bundle.OneShotOptions);
                var command = new TargetCreationOneShotCommand(journal, permits, coordinator, new() { Enabled = true });
                code = await command.RunAsync(permitId, local.Intent, binding, buffer, ct);
                commandCompleted = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !commandCompleted)
        { failure = "TargetCreationOneShotCancelled"; code = 130; }
        catch (Exception)
        {
            // Includes cleanup failure after a command receipt: do not reenter any lifecycle.
            // False physical flags in this host failure are unknown, not proof of no invocation.
            failure = "TargetCreationOneShotUnavailable"; code = 5;
        }
        if (failure is not null) return await Emit(output, failure, code);
        try { await output.WriteAsync(buffer.ToString()); }
        catch (Exception) { return 5; } // Never emit a fallback after a possibly partial write.
        return code;
    }

    private static async Task<int> Emit(TextWriter output, string status, int code)
    {
        try
        {
            await output.WriteLineAsync("{\"status\":\"" + status +
                "\",\"executable\":false,\"freshAuthority\":false,\"scheduledJobsMustRemainBlocked\":true,\"retryAllowed\":false" +
                ",\"physicalInvocationAttempted\":false,\"creationAcknowledged\":false}");
        }
        catch (Exception) { return 5; }
        return code;
    }
}
