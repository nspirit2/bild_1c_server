using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.TestFixtures;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuildSystem.AgentTests;

[Collection("Agent CLI subprocesses")]
public sealed class TargetRegistrationEvidenceDeliveryHostCommandTests
{
    private const string Command = "deliver-target-registration-evidence";
    private const string OperationText = "abcdef12-3456-7890-abcd-ef1234567890";
    private static readonly Guid Operation = Guid.ParseExact(OperationText, "D");
    private const string Section = "Agent:TargetRegistrationEvidenceDelivery";
    private const string Enabled = Section + ":Enabled";
    private const string Timeout = Section + ":TotalTimeoutSeconds";
    private const string Disabled = "TargetRegistrationEvidenceDeliveryDisabled";
    private const string Unavailable = "TargetRegistrationEvidenceDeliveryUnavailable";
    private const string Canary = "private-host-config-canary";

    [Fact]
    public void ExactCommandAndCanonicalOperationIdAreAccepted()
    {
        string[] args = [Command, OperationText];
        Assert.True(TargetRegistrationEvidenceDeliveryHostCommand.IsCommand(args));
        Assert.True(TargetRegistrationEvidenceDeliveryHostCommand.TryParse(args, out var operation));
        Assert.Equal(Operation, operation);
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        Array.Empty<string>(),
        new[] { Command },
        new[] { Command, OperationText, "--force" },
        new[] { Command, OperationText, "Agent:TargetRegistrationEvidenceDelivery:Enabled=true" },
        new[] { "Deliver-target-registration-evidence", OperationText },
        new[] { Command + " ", OperationText },
        new[] { Command, "" },
        new[] { Command, "not-a-guid" },
        new[] { Command, "00000000-0000-0000-0000-000000000000" },
        new[] { Command, "ABCDEF12-3456-7890-ABCD-EF1234567890" },
        new[] { Command, "abcdef12-3456-7890-ABCD-ef1234567890" },
        new[] { Command, "abcdef1234567890abcdef1234567890" },
        new[] { Command, "{" + OperationText + "}" },
        new[] { Command, " " + OperationText },
        new[] { Command, OperationText + "\n" }
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void InvalidArgumentsAreNotRecognized(string[] args)
    {
        Assert.False(TargetRegistrationEvidenceDeliveryHostCommand.IsCommand(args));
        Assert.False(TargetRegistrationEvidenceDeliveryHostCommand.TryParse(args, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("FaLsE")]
    public async Task DisabledIgnoresOtherFieldsAndDoesNotCreateStorage(string? enabled)
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "evidence-host-absent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fields = new Dictionary<string, string?>
            {
                [Section + ":Unknown:Nested"] = Canary,
                [Timeout] = Canary,
                [Timeout + ":Nested"] = Canary,
                ["Agent:DataDirectory"] = missingDirectory,
                ["Agent:ApiOrigin"] = "http://" + Canary + ".invalid/non-root"
            };
            if (enabled is not null) fields.Add(Enabled, enabled);
            using var output = new StringWriter();

            Assert.Equal(3, await TargetRegistrationEvidenceDeliveryHostCommand.RunAsync(Operation, Config(fields), output));
            AssertReceipt(output.ToString(), Disabled, missingDirectory);
            Assert.False(Directory.Exists(missingDirectory));
        }
        finally
        {
            // Only this test's unique private path, in case a regression creates it.
            var resolved = Path.GetFullPath(missingDirectory);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetDirectoryName(resolved));
            Assert.StartsWith("evidence-host-absent-", Path.GetFileName(resolved));
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("1", 1)]
    [InlineData("120", 120)]
    public void EnabledTimeoutBoundariesAndDefaultAreBoundWithoutIo(string? timeout, int expected)
    {
        var fields = new Dictionary<string, string?> { [Enabled] = "TrUe" };
        if (timeout is not null) fields.Add(Timeout, timeout);
        var options = TargetRegistrationEvidenceDeliveryHostCommand.BindDeliveryOptions(Config(fields));
        Assert.True(options.Enabled);
        Assert.Equal(expected, options.TotalTimeoutSeconds);
    }

    [Theory]
    [InlineData(Enabled, Canary)]
    [InlineData(Enabled, "1")]
    [InlineData(Enabled, "")]
    [InlineData(Enabled, " true ")]
    [InlineData(Enabled, "false ")]
    [InlineData(Enabled + ":Nested", Canary)]
    [InlineData(Section, Canary)]
    [InlineData(Section + ":" + Canary, "unexpected")]
    [InlineData(Timeout + ":Nested", Canary)]
    [InlineData(Timeout, "0")]
    [InlineData(Timeout, "121")]
    [InlineData(Timeout, Canary)]
    public async Task InvalidConfigurationIsRejectedByBinderAndRedactedByHost(string key, string value)
    {
        var config = Config(new() { [Enabled] = "true", [key] = value });
        var error = Assert.Throws<InvalidOperationException>(() =>
            TargetRegistrationEvidenceDeliveryHostCommand.BindDeliveryOptions(config));
        Assert.Equal("InvalidTargetRegistrationEvidenceDeliveryConfiguration", error.Message);
        Assert.Null(error.InnerException);
        using var output = new StringWriter();

        Assert.Equal(5, await TargetRegistrationEvidenceDeliveryHostCommand.RunAsync(Operation, config, output));
        AssertReceipt(output.ToString(), Unavailable);
    }

    [Fact]
    public async Task HostConfigurationFailureProducesOneRedactedReceipt()
    {
        using var output = new StringWriter();
        Assert.Equal(5, await TargetRegistrationEvidenceDeliveryHostCommand.WriteConfigurationFailureAsync(output));
        AssertReceipt(output.ToString(), Unavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputFailureIsNotRetried(bool configurationFailure)
    {
        using var output = new FailingWriter();
        var code = configurationFailure
            ? await TargetRegistrationEvidenceDeliveryHostCommand.WriteConfigurationFailureAsync(output)
            : await TargetRegistrationEvidenceDeliveryHostCommand.RunAsync(Operation, Config(new()), output);
        Assert.Equal(5, code);
        Assert.Equal(1, output.Attempts);
    }

    [Fact]
    public async Task EnabledReplayUsesOnlyTheExistingLocalAcknowledgement()
    {
        // There is no credential file, no real endpoint, and no unacknowledged evidence to send.
        var directory = Directory.CreateTempSubdirectory("evidence-host-replay-");
        try
        {
            Guid operation;
            TargetCreationRecord? before;
            TargetRegistrationEvidenceReport report;
            TargetRegistrationEvidenceDeliveryReceipt receipt;
            using (var journal = new AgentJournal(directory.FullName))
            {
                var (ack, _, _) = TargetRegistrationEvidenceDeliverySeed.SeedEvidence(journal);
                operation = ack.Intent.OperationId;
                before = journal.GetTargetCreationIntent(operation);
                report = journal.PrepareTargetRegistrationEvidenceReport(operation);
                receipt = new(report.ClaimId, operation, "Recorded", TargetRegistrationEvidenceDeliverySeed.Issued);
                journal.RecordTargetRegistrationEvidenceDeliveryAcknowledgement(operation, report, receipt);
            }
            var config = Config(new()
            {
                [Enabled] = "true",
                ["Agent:DataDirectory"] = directory.FullName,
                ["Agent:ApiOrigin"] = "https://localhost/"
            });
            using var output = new StringWriter();
            var code = await TargetRegistrationEvidenceDeliveryHostCommand.RunAsync(operation, config, output);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(0, code);
                AssertReceipt(output.ToString(), "TargetRegistrationEvidenceDeliveryAlreadyRecorded", operation.ToString("D"), directory.FullName);
            }
            else
            {
                Assert.Equal(5, code);
                AssertReceipt(output.ToString(), Unavailable, operation.ToString("D"), directory.FullName);
            }

            using var reopened = new AgentJournal(directory.FullName);
            Assert.Equal(before, reopened.GetTargetCreationIntent(operation));
            var saved = Assert.IsType<TargetRegistrationEvidenceDeliveryAcknowledgement>(reopened.GetTargetRegistrationEvidenceDeliveryAcknowledgement(operation));
            Assert.Equal(report, saved.Report);
            Assert.Equal(receipt, saved.Receipt);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "agent-token.dpapi")));
        }
        finally
        {
            // Exact directory allocated by CreateTempSubdirectory, not a configured path.
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ActualCliDefaultsOffWithoutStartingWorkerOrCreatingStorage()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "AgentFixture", "BuildSystem.Agent.dll");
        Assert.True(File.Exists(fixture), "The test build must copy the AgentFixture assembly.");
        Assert.True(File.Exists(Path.ChangeExtension(fixture, ".runtimeconfig.json")));
        var directory = Directory.CreateTempSubdirectory("evidence-host-cli-");
        try
        {
            var start = new ProcessStartInfo(FindDotnetHost())
            {
                WorkingDirectory = directory.FullName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(fixture);
            start.ArgumentList.Add(Command);
            start.ArgumentList.Add(OperationText);
            start.Environment.Clear();
            foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "DOTNET_ROOT_ARM64" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            start.Environment["DOTNET_CONTENTROOT"] = directory.FullName;
            start.Environment["Agent__DataDirectory"] = Path.Combine(directory.FullName, Canary);
            start.Environment["Agent__ApiOrigin"] = "http://" + Canary + ".invalid/non-root";
            start.Environment["Agent__TargetRegistrationEvidenceDelivery__Unknown__Nested"] = Canary;

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Agent process did not start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdout = CaptureAsync(process.StandardOutput, timeout.Token);
            var stderr = CaptureAsync(process.StandardError, timeout.Token);
            var readers = Task.WhenAll(stdout, stderr);
            try
            {
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(30));
                var streams = await readers.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(3, process.ExitCode);
                Assert.Empty(streams[1]);
                AssertReceipt(streams[0], Disabled, directory.FullName);
                Assert.Empty(directory.EnumerateFileSystemInfos());
            }
            finally
            {
                timeout.Cancel();
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                    }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    // Bound the pipe drain separately, including after kill or cancellation.
                    try { await readers.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) { }
                    catch (InvalidOperationException) { } // Output cap already fails the main path.
                }
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static void AssertReceipt(string output, string status, params string[] redacted)
    {
        Assert.EndsWith(Environment.NewLine, output, StringComparison.Ordinal);
        var line = output[..^Environment.NewLine.Length];
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain(Canary, line, StringComparison.Ordinal);
        Assert.DoesNotContain(OperationText, line, StringComparison.Ordinal);
        foreach (var value in redacted) Assert.DoesNotContain(value, line, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal(9, root.EnumerateObject().Count());
        Assert.Equal(status, root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("executable").GetBoolean());
        Assert.False(root.GetProperty("ownershipEstablished").GetBoolean());
        Assert.False(root.GetProperty("restoreAllowed").GetBoolean());
        Assert.False(root.GetProperty("retryAllowed").GetBoolean());
        Assert.False(root.GetProperty("freshAuthority").GetBoolean());
        Assert.True(root.GetProperty("requiresIndependentVerification").GetBoolean());
        Assert.True(root.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
        Assert.False(root.GetProperty("acknowledgementMayHaveCompleted").GetBoolean());
    }

    private static async Task<string> CaptureAsync(StreamReader reader, CancellationToken ct)
    {
        const int maxCharacters = 8192;
        var output = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (output.Length + count > maxCharacters) throw new InvalidOperationException("Agent output exceeded test bound.");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private static string FindDotnetHost()
    {
        // Same lookup order as the existing one-shot CLI fixture: explicit runner, current
        // muxer, then the .NET installation hosting this test or its configured root.
        var explicitHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (explicitHost is not null && File.Exists(explicitHost)) return Path.GetFullPath(explicitHost);
        var processPath = Environment.ProcessPath;
        if (processPath is not null && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location) })
            for (var parent = new DirectoryInfo(directory!); parent is not null; parent = parent.Parent)
            {
                var candidate = Path.Combine(parent.FullName, executable);
                if (File.Exists(candidate)) return candidate;
            }
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (dotnetRoot is not null && File.Exists(Path.Combine(dotnetRoot, executable)))
            return Path.Combine(dotnetRoot, executable);
        throw new InvalidOperationException("Could not locate dotnet host for process tests.");
    }

    private sealed class FailingWriter : StringWriter
    {
        public int Attempts { get; private set; }

        public override Task WriteLineAsync(string? value)
        {
            Attempts++;
            return Task.FromException(new IOException(Canary));
        }
    }
}
