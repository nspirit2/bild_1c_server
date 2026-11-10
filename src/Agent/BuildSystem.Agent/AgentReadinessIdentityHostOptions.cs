using Microsoft.Extensions.Configuration;

namespace BuildSystem.Agent;

/// <summary>Explicit probe configuration only; not service identity attestation or execution authority.</summary>
public sealed class AgentReadinessIdentityHostOptions
{
    public bool Enabled { get; init; }
    public Guid ExpectedAgentId { get; init; }
    public int HttpTimeoutSeconds { get; init; } = 30;
    public Uri ApiOrigin { get; private init; } = null!;
    public string DataDirectory { get; private init; } = "";

    public static AgentReadinessIdentityHostOptions Bind(IConfiguration configuration)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var section = configuration.GetSection("Agent:ReadinessIdentityProbe");
            if (section.Value is not null || section.GetSection(nameof(Enabled)).GetChildren().Any())
                throw new InvalidOperationException();
            if (!section.GetValue<bool>(nameof(Enabled))) return new();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { nameof(Enabled), nameof(ExpectedAgentId), nameof(HttpTimeoutSeconds) };
            if (section.GetChildren().Any(x => !allowed.Contains(x.Key) || x.GetChildren().Any()))
                throw new InvalidOperationException();
            var expected = section.GetValue<Guid>(nameof(ExpectedAgentId));
            var timeout = section.GetValue<int?>(nameof(HttpTimeoutSeconds)) ?? 30;
            var originText = configuration["Agent:ApiOrigin"];
            if (expected == Guid.Empty || timeout is < 1 or > 60 ||
                string.IsNullOrWhiteSpace(originText) || originText.Any(char.IsControl) ||
                !Uri.TryCreate(originText, UriKind.Absolute, out var origin) ||
                origin.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(origin.Host) ||
                origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
                throw new InvalidOperationException();
            var directory = configuration["Agent:DataDirectory"];
            if (string.IsNullOrWhiteSpace(directory) || directory.Length > 4096 || directory.Any(char.IsControl) ||
                !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new InvalidOperationException();
            var token = new FileInfo(Path.Combine(directory, "agent-token.dpapi"));
            // Metadata only; the host loads DPAPI only after explicit enablement, cancellation and OS gates.
            if (!token.Exists || token.Length is < 1 or > 16384) throw new InvalidOperationException();
            return new() { Enabled = true, ExpectedAgentId = expected, HttpTimeoutSeconds = timeout,
                ApiOrigin = origin, DataDirectory = directory };
        }
        catch (Exception) { throw new InvalidOperationException("InvalidAgentReadinessIdentityHostConfiguration"); }
    }

    public override string ToString() => "AgentReadinessIdentityHostOptions [details omitted]";
}