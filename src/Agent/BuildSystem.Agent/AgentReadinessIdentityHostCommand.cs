using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace BuildSystem.Agent;

/// <summary>Explicit identity observation only; never starts workers or authorizes execution.</summary>
public static class AgentReadinessIdentityHostCommand
{
    public static bool IsProbeCommand(string[] args) =>
        args is { Length: 1 } && string.Equals(args[0], "probe-agent-api-identity", StringComparison.Ordinal);

    public static async Task<int> RunAsync(IConfiguration configuration, TextWriter output,
        CancellationToken ct = default)
    {
        var status = "AgentReadinessIdentityUnavailable";
        var exitCode = 5;
        var observed = false;
        try
        {
            // Binding wins over cancellation and platform checks; disabled binding ignores other values.
            var options = AgentReadinessIdentityHostOptions.Bind(configuration);
            if (!options.Enabled)
            {
                status = "AgentReadinessIdentityDisabled";
                exitCode = 3;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                using (var client = new AgentReadinessIdentityHttpClient(new()
                {
                    Enabled = true,
                    BaseUri = options.ApiOrigin,
                    TimeoutSeconds = options.HttpTimeoutSeconds
                }, () => IdentityProbeTokenLoader.Load(Path.Combine(options.DataDirectory, "agent-token.dpapi"))))
                {
                    await client.ProbeAsync(options.ExpectedAgentId, ct).ConfigureAwait(false);
                }
                // No external output or positive observation before all client cleanup completes.
                ct.ThrowIfCancellationRequested();
                observed = true;
                status = "AgentReadinessIdentityObservedRequiresOperationalVerification";
                exitCode = 2;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "AgentReadinessIdentityCancelled";
            exitCode = 130;
        }
        catch (Exception) { }
        return await WriteAsync(output, status, observed, exitCode).ConfigureAwait(false);
    }

    public static Task<int> WriteConfigurationFailureAsync(TextWriter output) =>
        WriteAsync(output, "AgentReadinessIdentityUnavailable", false, 5);

    private static async Task<int> WriteAsync(TextWriter output, string status, bool observed, int exitCode)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                status,
                apiIdentityObserved = observed,
                executable = false,
                freshAuthority = false,
                readyForExecution = false,
                operationalVerificationRequired = true,
                scheduledJobsMustRemainBlocked = true,
                retryAllowed = false
            });
            await output.WriteLineAsync(json).ConfigureAwait(false);
            return exitCode;
        }
        catch (Exception) { return 5; } // One output attempt only: no fallback, raw error or retry.
    }
}
// End.
