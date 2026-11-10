using Microsoft.Extensions.Configuration;

namespace BuildSystem.Agent;

/// <summary>Offline, read-only metadata; never execution authority or operational verification.</summary>
public static class TargetCreationOneShotReadiness
{
    public static TargetCreationOneShotReadinessReport Evaluate(IConfiguration configuration)
    {
        var enabled = false;
        try
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var section = configuration.GetSection("Agent:TargetCreationOneShot");
            if (section.Value is not null || section.GetSection("Enabled").GetChildren().Any())
                throw new InvalidOperationException();
            enabled = section.GetValue<bool>("Enabled");
            // Preserve the actual opt-in and disabled binding semantics; never project Enabled=true.
            var options = TargetCreationOneShotHostOptions.Bind(configuration);
            if (!options.Enabled)
                return new(enabled: false, staticValid: false, artifactsPresent: false, disabled: true);

            var originText = configuration["Agent:ApiOrigin"];
            if (string.IsNullOrWhiteSpace(originText) || originText.Any(char.IsControl) ||
                !Uri.TryCreate(originText, UriKind.Absolute, out var origin) ||
                origin.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(origin.Host) ||
                origin.AbsolutePath != "/" || origin.Query.Length != 0 ||
                origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
                throw new InvalidOperationException();

            var directory = configuration["Agent:DataDirectory"];
            if (string.IsNullOrWhiteSpace(directory) || directory.Length > 4096 ||
                directory.Any(char.IsControl) || !Path.IsPathFullyQualified(directory) ||
                !Directory.Exists(directory))
                throw new InvalidOperationException();
            // Presence only: no SQLite open, token load, permission probe, or content reads.
            var artifacts = File.Exists(Path.Combine(directory, "agent-journal.sqlite")) &&
                File.Exists(Path.Combine(directory, "agent-token.dpapi"));
            return new(enabled, staticValid: artifacts, artifactsPresent: artifacts);
        }
        catch (Exception)
        {
            // No configuration values, paths, identifiers, or exception details leave this boundary.
            return new(enabled, staticValid: false, artifactsPresent: false);
        }
    }
}

/// <summary>
/// Inert fixed metadata, safe to serialize. False configured-enabled means opt-in was not confirmed;
/// static checks and file presence do not establish credential validity, authority, or safe execution.
/// </summary>
public sealed record TargetCreationOneShotReadinessReport
{
    internal TargetCreationOneShotReadinessReport(bool enabled, bool staticValid,
        bool artifactsPresent, bool disabled = false)
    {
        Status = disabled ? "DisabledNotAssessed" : staticValid
            ? "StaticChecksPassedRequiresOperationalVerification" : "InvalidConfiguration";
        ExecutionConfiguredEnabled = enabled;
        PlatformSupported = OperatingSystem.IsWindows();
        StaticConfigurationValid = staticValid;
        ExistingLocalArtifactsPresent = artifactsPresent;
    }

    public string Status { get; }
    public bool ExecutionConfiguredEnabled { get; }
    public bool PlatformSupported { get; }
    public bool StaticConfigurationValid { get; }
    public bool ExistingLocalArtifactsPresent { get; }
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool RetryAllowed => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool ReadyForExecution => false;
    public bool OperationalVerificationRequired => true;

    public override string ToString() => "TargetCreationOneShotReadinessReport [details omitted]";
}