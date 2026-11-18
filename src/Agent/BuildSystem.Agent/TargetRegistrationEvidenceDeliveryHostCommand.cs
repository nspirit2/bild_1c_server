using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace BuildSystem.Agent;

/// <summary>One explicit delivery of saved historical evidence; no worker, observation, or physical operation.</summary>
public static class TargetRegistrationEvidenceDeliveryHostCommand
{
    private const string Command = "deliver-target-registration-evidence";
    private const string StatusPrefix = "TargetRegistrationEvidenceDelivery";

    public static bool IsCommand(string[] args) => TryParse(args, out _);

    public static bool TryParse(string[] args, out Guid operationId)
    {
        operationId = Guid.Empty;
        if (args is not { Length: 2 } || !string.Equals(args[0], Command, StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1], "D", out var id) || id == Guid.Empty ||
            !string.Equals(args[1], id.ToString("D"), StringComparison.Ordinal)) return false;
        operationId = id;
        return true;
    }

    /// <summary>Validates only the command's opt-in and timeout, without touching filesystem or transport.</summary>
    public static TargetRegistrationEvidenceDeliveryOptions BindDeliveryOptions(IConfiguration configuration)
    {
        try
        {
            var section = configuration.GetSection("Agent:TargetRegistrationEvidenceDelivery");
            if (section.Value is not null) throw new InvalidOperationException();
            var members = section.GetChildren().ToArray();
            var enabled = members.SingleOrDefault(child => NameIs(child, "Enabled"));
            if (enabled is null) return new();
            if (enabled.GetChildren().Any()) throw new InvalidOperationException();
            var flag = enabled.Value;
            if (string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)) return new();
            if (!string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();

            var timeout = 30;
            foreach (var member in members)
            {
                if (NameIs(member, "Enabled")) continue;
                if (!NameIs(member, "TotalTimeoutSeconds") || member.GetChildren().Any() ||
                    member.Value is not { Length: > 0 } value || !value.All(char.IsAsciiDigit) ||
                    !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timeout) ||
                    timeout is < 1 or > 120) throw new InvalidOperationException();
            }
            if (members.Length > 2) throw new InvalidOperationException();
            return new() { Enabled = true, TotalTimeoutSeconds = timeout };
        }
        catch (Exception) { throw new InvalidOperationException("InvalidTargetRegistrationEvidenceDeliveryConfiguration"); }
    }

    /// <summary>
    /// Disabled configuration wins over cancellation and does not open dependencies. Enabled cancellation
    /// before local acknowledgement entry is unavailable; it is not proof the server did not accept a report.
    /// Synchronous journal work is not preemptible. If acknowledgement may have completed, including after
    /// a new delivery followed by host cleanup failure or cancellation, report uncertainty with no retry.
    /// A historical, already recorded acknowledgement is not a write attempted by this invocation.
    /// </summary>
    public static async Task<int> RunAsync(Guid operationId, IConfiguration configuration, TextWriter output,
        CancellationToken ct = default)
    {
        var acknowledgementMayHaveCompleted = false;
        var status = "Unavailable";
        var exitCode = 5;
        try
        {
            var options = BindDeliveryOptions(configuration);
            if (!options.Enabled)
            {
                status = "Disabled";
                exitCode = 3;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                if (operationId == Guid.Empty || !OperatingSystem.IsWindows()) throw new InvalidOperationException();
                var origin = ReadOrigin(configuration);
                var directory = ReadWindowsDirectory(configuration);
                var journalPath = Path.Combine(directory, "agent-journal.sqlite");
                var tokenPath = Path.Combine(directory, "agent-token.dpapi");
                ct.ThrowIfCancellationRequested();

                // The directory is provisioned and ACL-protected by the operator. A read-only handle
                // denies ordinary deletion/replacement until AgentJournal has finished. It does not
                // replace the production lock, which AgentJournal takes before opening SQLite, nor
                // does it defend against an administrator changing the filesystem namespace.
                using (var existing = OpenExistingSqlite(journalPath))
                {
                    ct.ThrowIfCancellationRequested();
                    // The normal journal constructor may initialize/maintain its schema in the existing file.
                    using var journal = new AgentJournal(directory);
                    using var transport = new TargetRegistrationEvidenceHttpClient(new()
                    {
                        Enabled = options.Enabled,
                        BaseUri = origin,
                        TimeoutSeconds = options.TotalTimeoutSeconds
                    }, () => TokenStore.Load(tokenPath));
                    var delivery = new TargetRegistrationEvidenceDelivery(journal, transport, options);
                    try
                    {
                        acknowledgementMayHaveCompleted = (await delivery.DeliverOperationAsync(operationId, ct)
                            .ConfigureAwait(false)).DeliveredThisCall;
                    }
                    catch (TargetRegistrationEvidenceDeliveryException e) when (e.AcknowledgementMayHaveCompleted ||
                        e.Code == StatusPrefix + "AcknowledgementUnconfirmed")
                    {
                        // Capture before disposing the dependencies, which could mask this exception.
                        acknowledgementMayHaveCompleted = true;
                        throw;
                    }
                }

                // No positive status before cleanup, or if cancellation became visible during cleanup.
                ct.ThrowIfCancellationRequested();
                status = acknowledgementMayHaveCompleted ? "Delivered" : "AlreadyRecorded";
                acknowledgementMayHaveCompleted = false;
                exitCode = 0;
            }
        }
        catch (Exception)
        {
            status = acknowledgementMayHaveCompleted ? "AcknowledgementUnconfirmed" : "Unavailable";
        }
        return await WriteAsync(output, status, acknowledgementMayHaveCompleted, exitCode).ConfigureAwait(false);
    }

    public static Task<int> WriteConfigurationFailureAsync(TextWriter output) => WriteAsync(output, "Unavailable", false, 5);

    private static bool NameIs(IConfigurationSection section, string name) =>
        string.Equals(section.Key, name, StringComparison.OrdinalIgnoreCase);

    private static string ReadScalar(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        if (section.GetChildren().Any() || section.Value is not { Length: > 0 } value) throw new InvalidOperationException();
        return value;
    }

    private static Uri ReadOrigin(IConfiguration configuration)
    {
        var value = ReadScalar(configuration, "Agent:ApiOrigin");
        const string scheme = "https://";
        // Check raw syntax too: Uri normalization must not turn a configured path into the root.
        if (!value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ||
            value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '?' or '#' or '@') ||
            (value.IndexOf('/', scheme.Length) is var slash && slash >= 0 && slash != value.Length - 1) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString() ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException();
        return uri;
    }

    private static string ReadWindowsDirectory(IConfiguration configuration)
    {
        var value = ReadScalar(configuration, "Agent:DataDirectory").Replace('/', '\\');
        if (value.Length > 4096 || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException();
        value = Path.TrimEndingDirectorySeparator(value);
        // Drive-qualified local paths only: no UNC/device namespaces or mapped network drives.
        if (value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\' ||
            (value.Length > 3 && value[3..].Split('\\').Any(component => !UnambiguousComponent(component))))
            throw new InvalidOperationException();
        var fullPath = Path.GetFullPath(value);
        if (!string.Equals(value, Path.TrimEndingDirectorySeparator(fullPath), StringComparison.Ordinal) ||
            new DriveInfo(value[..3]).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new InvalidOperationException();
        for (DirectoryInfo? directory = new(fullPath); directory is not null; directory = directory.Parent)
        {
            var attributes = File.GetAttributes(directory.FullName);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new InvalidOperationException();
        }
        return fullPath;
    }

    private static bool UnambiguousComponent(string component)
    {
        if (component.Length == 0 || char.IsWhiteSpace(component[0]) || char.IsWhiteSpace(component[^1]) ||
            component[^1] == '.' || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        // A reserved Win32 device name still aliases the device when followed by an extension.
        var dot = component.IndexOf('.');
        var stem = (dot < 0 ? component : component[..dot]).TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    private static FileStream OpenExistingSqlite(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException();
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            // Reject an empty placeholder or a non-SQLite file rather than bootstrap a new database.
            Span<byte> signature = stackalloc byte[16];
            if (file.Length < 100) throw new InvalidOperationException();
            file.ReadExactly(signature);
            if (!signature.SequenceEqual("SQLite format 3\0"u8)) throw new InvalidOperationException();
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    private static async Task<int> WriteAsync(TextWriter output, string status, bool acknowledgementMayHaveCompleted, int exitCode)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                status = StatusPrefix + status,
                executable = false,
                ownershipEstablished = false,
                restoreAllowed = false,
                retryAllowed = false,
                freshAuthority = false,
                requiresIndependentVerification = true,
                scheduledJobsMustRemainBlocked = true,
                acknowledgementMayHaveCompleted
            });
            await output.WriteLineAsync(json).ConfigureAwait(false);
            return exitCode;
        }
        catch (Exception) { return 5; } // One output attempt only, even after a partial write.
    }
}
