using System.Globalization;
using System.Net;
using BuildSystem.Contracts;
using Microsoft.Extensions.Configuration;

namespace BuildSystem.Agent;

/// <summary>
/// Immutable local observation configuration snapshot. Existing operator-owned paths are permitted;
/// existence checks are not ACL attestation, symlink protection, or permission to mutate a target.
/// </summary>
public sealed class TargetObservationHostOptions
{
    public bool Enabled { get; init; }
    public Guid AgentId { get; init; }
    public Guid CanonicalClusterId { get; init; }
    public Guid RacClusterId { get; init; }
    public Guid CanonicalSqlServerId { get; init; }
    public Guid RacEndpointMappingId { get; init; }
    public Guid SqlEndpointMappingId { get; init; }
    public string RacExecutable { get; init; } = "";
    public string RacWorkingDirectory { get; init; } = "";
    public string RacEndpoint { get; init; } = "";
    public string SqlServer { get; init; } = "";
    public string ExpectedSqlMachineName { get; init; } = "";
    public string ExpectedSqlInstanceName { get; init; } = "MSSQLSERVER";
    public int TimeoutSeconds { get; init; } = 30;
    public int TotalTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Disabled mode reads only the section shape and Enabled value: other members (including unknown
    /// members) are deliberately ignored, with no path or credential IO. Enabled mode binds strictly.
    /// Configuration failures never expose values or provider diagnostics.
    /// </summary>
    public static TargetObservationHostOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        try
        {
            var section = configuration.GetSection("Agent:TargetObservation");
            if (section.Value is not null || section.GetSection(nameof(Enabled)).GetChildren().Any())
                throw new InvalidOperationException();
            if (!section.GetValue<bool>(nameof(Enabled))) return new();
            // All supported members are scalar; reject nested shapes even where the binder would
            // otherwise retain a string property's default value and silently ignore its children.
            if (section.GetChildren().Any(member => member.GetChildren().Any()))
                throw new InvalidOperationException();
            var options = section.Get<TargetObservationHostOptions>(binder =>
                binder.ErrorOnUnknownConfiguration = true);
            if (options is null || !options.Valid()) throw new InvalidOperationException();
            return options;
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Invalid target observation host configuration.");
        }
    }

    private bool Valid() => Enabled && AgentId != Guid.Empty && CanonicalClusterId != Guid.Empty && RacClusterId != Guid.Empty &&
        CanonicalSqlServerId != Guid.Empty && RacEndpointMappingId != Guid.Empty && SqlEndpointMappingId != Guid.Empty &&
        SafePath(RacExecutable) && File.Exists(RacExecutable) &&
        string.Equals(Path.GetFileName(RacExecutable), "rac.exe", StringComparison.OrdinalIgnoreCase) &&
        SafePath(RacWorkingDirectory) && Directory.Exists(RacWorkingDirectory) &&
        ValidRacEndpoint(RacEndpoint) && ValidSqlServer(SqlServer) &&
        ValidIdentity(ExpectedSqlMachineName) && ValidIdentity(ExpectedSqlInstanceName) &&
        TimeoutSeconds is >= 1 and <= 60 && TotalTimeoutSeconds is >= 1 and <= 120;

    private static bool SafePath(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 4096 && !value.Any(char.IsControl) && Path.IsPathFullyQualified(value);

    private static bool ValidIdentity(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 && !value.Any(char.IsControl);

    private static bool Port(string value) => value.Length > 0 && value.All(char.IsAsciiDigit) &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535;

    private static bool Host(string value) => value.Length > 0 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
        Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.IPv4;

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
        return colon > 0 && Host(value[..colon]) && Port(value[(colon + 1)..]);
    }

    private static bool ValidSqlServer(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 255 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == ';')) return false;
        var slash = value.IndexOf('\\');
        var comma = value.IndexOf(',');
        if (slash >= 0)
            return comma < 0 && Host(value[..slash]) && value[(slash + 1)..].Length is > 0 and <= 128 &&
                value[(slash + 1)..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '$');
        return comma >= 0 ? Host(value[..comma]) && Port(value[(comma + 1)..]) : Host(value);
    }

    public override string ToString() => "TargetObservationHostOptions [details omitted]";
}

/// <summary>
/// Checks local assignment pins before the inner executor can read credentials or contact targets.
/// This is an executor boundary, not a coordinator preflight: coordinator GET and assignment journal
/// activity (and historical report retrieval) may precede it. It grants no execution authority.
/// </summary>
public sealed class TargetObservationPinnedExecutor : ITargetObservationExecutor
{
    private readonly TargetObservationHostOptions options;
    private readonly ITargetObservationExecutor inner;

    public TargetObservationPinnedExecutor(TargetObservationHostOptions options, ITargetObservationExecutor inner)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<ReportTargetObservation> ObserveAsync(TargetObservationAssignment assignment, CancellationToken ct = default)
    {
        if (!options.Enabled || assignment is null || assignment.ObservationRequestId == Guid.Empty ||
            assignment.RestoreIntentId == Guid.Empty || assignment.OperationId == Guid.Empty ||
            assignment.EnvironmentId == Guid.Empty || assignment.StageJobId == Guid.Empty || assignment.FencingToken <= 0 ||
            assignment.ReservationId == Guid.Empty || assignment.TargetIncarnationId == Guid.Empty ||
            assignment.AgentId == Guid.Empty || assignment.AgentId != options.AgentId ||
            assignment.CanonicalClusterId == Guid.Empty || assignment.CanonicalClusterId != options.CanonicalClusterId ||
            assignment.CanonicalSqlServerId == Guid.Empty || assignment.CanonicalSqlServerId != options.CanonicalSqlServerId ||
            assignment.RacEndpointMappingId == Guid.Empty || assignment.RacEndpointMappingId != options.RacEndpointMappingId ||
            assignment.SqlEndpointMappingId == Guid.Empty || assignment.SqlEndpointMappingId != options.SqlEndpointMappingId ||
            assignment.Executable || !assignment.ScheduledJobsMustRemainBlocked)
            throw new TargetObservationExecutionException();
        ct.ThrowIfCancellationRequested();
        return inner.ObserveAsync(assignment, ct);
    }
}
