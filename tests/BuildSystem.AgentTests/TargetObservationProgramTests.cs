using System.Diagnostics;
using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetObservationProgramTests
{
    [Theory]
    [InlineData(false, false, 4, "TargetObservationDisabled")]
    [InlineData(true, false, 5, "TargetObservationConfigurationOrStorageFailure")]
    [InlineData(true, true, 64, "UnsupportedAgentCommand")]
    public async Task ExecutableRejectsUnsafeStartupWithoutWorkerOrTargetIo(bool malformed, bool extraFlag, int exit, string status)
    {
        var directory = Path.Combine(Path.GetTempPath(), "observation-program-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (malformed) File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{ private-sensitive-invalid-json");
            var start = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                WorkingDirectory = directory,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            // The test project uses AspNetCore shared framework/pruned package graph; use its
            // runtimeconfig/deps to resolve copied Agent dependencies rather than Agent's runtimeconfig.
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "BuildSystem.AgentTests.runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "BuildSystem.AgentTests.deps.json"));
            start.ArgumentList.Add(typeof(AgentJournal).Assembly.Location);
            start.ArgumentList.Add("observe-target"); start.ArgumentList.Add(Guid.NewGuid().ToString("D"));
            if (extraFlag) start.ArgumentList.Add("--force");
            // Isolate inherited observation/host configuration; deliberately invalid paths must never be read.
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("Agent", StringComparison.OrdinalIgnoreCase) || k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)).ToArray())
                if (key != "DOTNET_ROOT" && key != "DOTNET_HOST_PATH") start.Environment.Remove(key);
            start.Environment["DOTNET_CONTENTROOT"] = directory;
            start.Environment["Agent__TargetObservation__Enabled"] = "false";
            start.Environment["Agent__DataDirectory"] = "private-untrusted-relative-directory";
            start.Environment["Agent__ArchiveStageEnabled"] = "true";
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } }
            Assert.Equal(exit, process.ExitCode);
            Assert.Equal(status, ((await stdout) + (await stderr)).Trim());
            Assert.Empty(Directory.GetDirectories(directory));
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (!Path.GetFileName(resolved).StartsWith("observation-program-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(resolved, true);
        }
    }
}
// End.
