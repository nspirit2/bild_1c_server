using Microsoft.Extensions.Configuration;

namespace BuildSystem.Agent;

/// <summary>Explicit metadata-only host. Never constructs a worker or executes a target command.</summary>
public static class TargetCreationStartHostCommand
{
    public static async Task<int> RunAsync(Guid permitId, IConfiguration configuration, TextWriter output,
        CancellationToken ct = default)
    {
        int code;
        string status;
        using var bufferedOutput = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            ct.ThrowIfCancellationRequested();
            var options = TargetCreationStartHostOptions.Bind(configuration);
            if (!options.Enabled) return await Emit(output, "TargetCreationStartDisabled", 3);
            if (permitId == Guid.Empty || !OperatingSystem.IsWindows()) throw new InvalidOperationException();
            var directory = configuration["Agent:DataDirectory"];
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
                directory.Any(char.IsControl) || !Directory.Exists(directory) ||
                !File.Exists(Path.Combine(directory, "agent-journal.sqlite"))) throw new InvalidOperationException();
            if (!Uri.TryCreate(configuration["Agent:ApiOrigin"], UriKind.Absolute, out var origin))
                throw new InvalidOperationException();
            // AgentJournal owns its existing exclusive journal lock. Stop other worker/command processes.
            using var journal = new AgentJournal(directory);
            var local = journal.GetTargetCreationIntent(options.OperationId) ?? throw new InvalidOperationException();
            var binding = journal.GetTargetCreationPermitBinding(options.OperationId) ?? throw new InvalidOperationException();
            if (binding.PermitId != permitId || binding.AgentId != options.AgentId ||
                local.Intent.CanonicalClusterId != options.CanonicalClusterId ||
                local.Intent.CanonicalSqlServerId != options.CanonicalSqlServerId ||
                binding.CredentialId != options.CredentialId || binding.EndpointMappingId != options.EndpointMappingId)
                throw new InvalidOperationException();
            using var transport = new TargetCreationPermitHttpClient(new()
            { BaseUri = origin, Enabled = true, TimeoutSeconds = options.TimeoutSeconds },
                () => TokenStore.Load(Path.Combine(directory, "agent-token.dpapi")));
            var command = new TargetCreationStartCommand(journal, transport, transport, new() { Enabled = true });
            code = await command.RunAsync(permitId, local.Intent, binding, bufferedOutput, ct);
            status = ""; // Dependencies dispose before the single external output attempt.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { status = "TargetCreationStartCancelled"; code = 130; }
        catch (Exception) { status = "TargetCreationStartUnavailable"; code = 5; }
        if (status.Length != 0) return await Emit(output, status, code);
        try { await output.WriteAsync(bufferedOutput.ToString()); }
        catch (Exception) { return 5; }
        return code;
    }

    private static async Task<int> Emit(TextWriter output, string status, int code)
    {
        try { await output.WriteLineAsync("{\"status\":\"" + status + "\",\"executable\":false,\"scheduledJobsMustRemainBlocked\":true,\"retryAllowed\":false}"); }
        catch (Exception) { return 5; }
        return code;
    }
}

/// <summary>Trusted selected operation/mapping pins, not a permit-discovery channel or execution policy.</summary>
public sealed class TargetCreationStartHostOptions
{
    public bool Enabled { get; init; }
    public Guid OperationId { get; init; }
    public Guid AgentId { get; init; }
    public Guid CanonicalClusterId { get; init; }
    public Guid CanonicalSqlServerId { get; init; }
    public Guid CredentialId { get; init; }
    public Guid EndpointMappingId { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public override string ToString() => "TargetCreationStartHostOptions [NONEXECUTABLE]";

    public static TargetCreationStartHostOptions Bind(IConfiguration configuration)
    {
        try
        {
            var section = configuration.GetSection("Agent:TargetCreationStartMetadata");
            if (section.Value is not null || section.GetSection(nameof(Enabled)).GetChildren().Any()) throw new InvalidOperationException();
            if (!section.GetValue<bool>(nameof(Enabled))) return new();
            if (section.GetChildren().Any(x => x.GetChildren().Any())) throw new InvalidOperationException();
            var options = section.Get<TargetCreationStartHostOptions>(b => b.ErrorOnUnknownConfiguration = true);
            if (options is null || !options.Enabled || options.TimeoutSeconds is < 1 or > 60 ||
                new[] { options.OperationId, options.AgentId, options.CanonicalClusterId, options.CanonicalSqlServerId,
                    options.CredentialId, options.EndpointMappingId }.Any(x => x == Guid.Empty)) throw new InvalidOperationException();
            return options;
        }
        catch (Exception) { throw new InvalidOperationException("InvalidTargetCreationStartHostConfiguration"); }
    }
}