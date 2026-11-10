using System.Globalization;
using System.Net;
using BuildSystem.Agent.Tools;
using Microsoft.Extensions.Configuration;

namespace BuildSystem.Agent;

/// <summary>
/// Dormant operator configuration, not creation authority. Existing paths are checked, not attested
/// for ACLs, symlinks or ownership. Binding never loads credentials, invokes a tool or contacts SQL.
/// </summary>
public sealed class TargetCreationOneShotHostOptions
{
    public const string TrustedToolId = "target-creation-rac";
    public bool Enabled { get; init; }
    public bool ExclusiveNamespaceConfirmed { get; init; }
    public Guid OperationId { get; init; }
    public Guid AgentId { get; init; }
    public Guid CredentialId { get; init; }
    public Guid EndpointMappingId { get; init; }
    public Guid RacObservationMappingId { get; init; }
    public Guid SqlObservationMappingId { get; init; }
    public Guid CanonicalClusterId { get; init; }
    public Guid RacClusterId { get; init; }
    public Guid CanonicalSqlServerId { get; init; }
    public string RacExecutable { get; init; } = "";
    public string RacWorkingDirectory { get; init; } = "";
    public string RacEndpoint { get; init; } = "";
    public string SqlServer { get; init; } = "";
    public string ExpectedSqlMachineName { get; init; } = "";
    public string ExpectedSqlInstanceName { get; init; } = "MSSQLSERVER";
    public int HttpTimeoutSeconds { get; init; } = 30;
    public int RacTimeoutSeconds { get; init; } = 30;
    public int SqlTimeoutSeconds { get; init; } = 30;
    public int TotalObservationTimeoutSeconds { get; init; } = 60;
    public int CreationTimeoutSeconds { get; init; } = 120;

    /// <summary>Disabled binding ignores all other members; enabled binding accepts only flat known scalars.</summary>
    public static TargetCreationOneShotHostOptions Bind(IConfiguration configuration)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var section = configuration.GetSection("Agent:TargetCreationOneShot");
            if (section.Value is not null || section.GetSection(nameof(Enabled)).GetChildren().Any())
                throw new InvalidOperationException();
            if (!section.GetValue<bool>(nameof(Enabled))) return new();
            if (section.GetChildren().Any(member => member.GetChildren().Any()))
                throw new InvalidOperationException();
            var options = section.Get<TargetCreationOneShotHostOptions>(binder =>
                binder.ErrorOnUnknownConfiguration = true);
            if (options is null || !options.Enabled || !options.ExclusiveNamespaceConfirmed ||
                options.OperationId == Guid.Empty || options.CredentialId == Guid.Empty ||
                options.HttpTimeoutSeconds is < 1 or > 60 || !ValidRacEndpoint(options.RacEndpoint) ||
                !SafePath(options.RacExecutable) || !File.Exists(options.RacExecutable) ||
                !string.Equals(Path.GetFileName(options.RacExecutable), "rac.exe", StringComparison.OrdinalIgnoreCase) ||
                !SafePath(options.RacWorkingDirectory) || !Directory.Exists(options.RacWorkingDirectory))
                throw new InvalidOperationException();

            // Reuse actual adapter validation rather than maintaining another endpoint/identity grammar.
            // Construction only snapshots policy: the provider must never be called here, nor a process run.
            var runner = new ProcessRunner(new ProcessRunnerOptions
            {
                TrustedTools = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [TrustedToolId] = options.RacExecutable
                },
                AllowedWorkspaceRoots = [options.RacWorkingDirectory],
                InheritedEnvironmentVariables = ["SystemRoot", "WINDIR", "TEMP", "TMP"]
            });
            _ = TargetCreationTrustedAdapters.Create(options.ToAdapterOptions(), runner,
                _ => throw new InvalidOperationException("Target creation binding cannot open a provider."));
            return options;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("InvalidTargetCreationOneShotHostConfiguration");
        }
    }

    /// <summary>Inert projection; opt-in is preserved, never manufactured by conversion.</summary>
    public TargetCreationTrustedAdapterOptions ToAdapterOptions() => new()
    {
        Enabled = Enabled, ExclusiveNamespaceConfirmed = ExclusiveNamespaceConfirmed,
        TrustedAgentId = AgentId, CanonicalClusterId = CanonicalClusterId, RacClusterId = RacClusterId,
        CanonicalSqlServerId = CanonicalSqlServerId, EndpointMappingId = EndpointMappingId,
        RacObservationMappingId = RacObservationMappingId, SqlObservationMappingId = SqlObservationMappingId,
        TrustedRacToolId = TrustedToolId, RacEndpoint = RacEndpoint, WorkingDirectory = RacWorkingDirectory,
        SqlServer = SqlServer, ExpectedSqlMachineName = ExpectedSqlMachineName,
        ExpectedSqlInstanceName = ExpectedSqlInstanceName, RacTimeoutSeconds = RacTimeoutSeconds,
        SqlTimeoutSeconds = SqlTimeoutSeconds, TotalObservationTimeoutSeconds = TotalObservationTimeoutSeconds,
        CreationTimeoutSeconds = CreationTimeoutSeconds
    };

    /// <summary>Read-only observation projection for fixed SQL connection construction and local pins.</summary>
    public TargetObservationHostOptions ToObservationOptions() => new()
    {
        Enabled = Enabled, AgentId = AgentId, CanonicalClusterId = CanonicalClusterId,
        RacClusterId = RacClusterId, CanonicalSqlServerId = CanonicalSqlServerId,
        RacEndpointMappingId = RacObservationMappingId, SqlEndpointMappingId = SqlObservationMappingId,
        RacExecutable = RacExecutable, RacWorkingDirectory = RacWorkingDirectory, RacEndpoint = RacEndpoint,
        SqlServer = SqlServer, ExpectedSqlMachineName = ExpectedSqlMachineName,
        ExpectedSqlInstanceName = ExpectedSqlInstanceName, TimeoutSeconds = SqlTimeoutSeconds,
        TotalTimeoutSeconds = TotalObservationTimeoutSeconds
    };

    private static bool SafePath(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 4096 && !value.Any(char.IsControl) && Path.IsPathFullyQualified(value);

    // The lower-level recipe accepts opaque endpoints. The host requires an explicit bounded port.
    private static bool ValidRacEndpoint(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 255 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            return close > 1 && close + 1 < value.Length && value[close + 1] == ':' &&
                IPAddress.TryParse(value[1..close], out var address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && Port(value[(close + 2)..]);
        }
        var colon = value.IndexOf(':');
        return colon > 0 && value[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
            Uri.CheckHostName(value[..colon]) is UriHostNameType.Dns or UriHostNameType.IPv4 && Port(value[(colon + 1)..]);
    }

    private static bool Port(string value) => value.Length > 0 && value.All(char.IsAsciiDigit) &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535;

    public override string ToString() => "TargetCreationOneShotHostOptions [details omitted]";
}
