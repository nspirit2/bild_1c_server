using System.Diagnostics;
using System.Text;

namespace BuildSystem.Agent.Tools;

public sealed class ProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, string> tools;
    private readonly string[] roots;
    private readonly string[] environment;
    private readonly int lineLimit, outputLimit;
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
        { "cmd", "powershell", "pwsh", "bash", "sh", "ash", "dash", "zsh", "csh", "tcsh", "ksh", "fish", "busybox", "wsl", "wscript", "cscript" };

    public ProcessRunner(ProcessRunnerOptions options)
    {
        tools = new(options.TrustedTools, StringComparer.Ordinal);
        roots = options.AllowedWorkspaceRoots.Select(ValidateDirectory).ToArray();
        environment = options.InheritedEnvironmentVariables.ToArray();
        lineLimit = options.MaxLineCharacters;
        outputLimit = options.MaxOutputCharacters;
        if (roots.Length == 0 || lineLimit is < 1 or > 65536 || outputLimit is < 1 or > 16777216)
            throw new ArgumentException("invalid_policy");
        foreach (var path in tools.Values)
            if (!Path.IsPathFullyQualified(path) || Shells.Contains(Path.GetFileNameWithoutExtension(path)))
                throw new ArgumentException("invalid_tool_policy");
        // Only explicit benign names: allowing arbitrary names admits loader/startup injection.
        foreach (var name in environment)
            if (name is not ("SystemRoot" or "WINDIR" or "TEMP" or "TMP" or "TMPDIR" or "HOME" or "USERPROFILE" or "LANG" or "TZ")
                && !name.StartsWith("LC_", StringComparison.Ordinal))
                throw new ArgumentException("invalid_environment_policy");
    }

    public async Task<ProcessResult> RunAsync(ProcessCommand command, Func<ProcessOutput, Task>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        string executable, directory;
        ProcessArgument[] arguments;
        string[] secrets;
        try
        {
            if (command.Timeout <= TimeSpan.Zero || command.Timeout > TimeSpan.FromHours(24)
                || !tools.TryGetValue(command.TrustedToolId, out executable!)) throw new ArgumentException();
            directory = ValidateDirectory(command.WorkingDirectory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!roots.Any(root => directory.Equals(root, comparison)
                || directory.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison)))
                throw new ArgumentException();
            arguments = command.Arguments.ToArray();
            if (arguments.Any(a => a.Value is null || a.Value.Contains('\0'))) throw new ArgumentException();
            secrets = (command.KnownSecrets ?? []).Concat(arguments.Where(a => a.Sensitive).Select(a => a.Value))
                .Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
            if (secrets.Length > 128 || secrets.Any(s => s.Length > 8192) || secrets.Sum(s => s.Length) > 65536)
                throw new ArgumentException();
        }
        catch { throw new ProcessRunnerException("invalid_command"); }
        if (cancellationToken.IsCancellationRequested)
            return new(null, null, ProcessCompletion.Cancelled, false);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = directory,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument.Value);
        start.Environment.Clear();
        foreach (var name in environment)
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new InvalidOperationException(); }
        catch { throw new ProcessRunnerException("start_failed"); }
        var pid = process.Id;
        using var timeout = new CancellationTokenSource(command.Timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = outputLimit;
        var truncated = 0;
        var callbackLock = new SemaphoreSlim(1, 1);
        async Task Emit(ProcessOutputStream stream, string text)
        {
            // Serialize callbacks while retaining only a bounded block per stream.
            try
            {
                await callbackLock.WaitAsync(stop.Token).ConfigureAwait(false);
                try
                {
                    if (budget == 0) { Interlocked.Exchange(ref truncated, 1); return; }
                    if (text.Length > budget) { text = text[..budget]; Interlocked.Exchange(ref truncated, 1); }
                    budget -= text.Length;
                    if (onOutput is not null)
                        await Task.Run(() => onOutput(new(stream, text)), CancellationToken.None)
                            .WaitAsync(stop.Token).ConfigureAwait(false);
                }
                finally { callbackLock.Release(); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch { failure.TrySetResult("callback_failed"); stop.Cancel(); }
        }
        async Task Drain(StreamReader reader, ProcessOutputStream stream)
        {
            var filter = new SecretFilter(secrets);
            var line = new StringBuilder();
            async Task Append(string safe)
            {
                foreach (var ch in safe)
                {
                    line.Append(ch);
                    if (line.Length >= lineLimit || ch == '\n')
                    {
                        await Emit(stream, line.ToString()).ConfigureAwait(false);
                        line.Clear();
                    }
                }
            }
            var buffer = new char[2048];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), stop.Token).ConfigureAwait(false)) != 0)
                {
                    if (budget == 0 || stop.IsCancellationRequested) { Interlocked.Exchange(ref truncated, 1); continue; }
                    await Append(filter.Push(buffer.AsSpan(0, count).ToString(), false)).ConfigureAwait(false);
                }
                await Append(filter.Push("", true)).ConfigureAwait(false);
                if (line.Length > 0) await Emit(stream, line.ToString()).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { Interlocked.Exchange(ref truncated, 1); }
            catch { failure.TrySetResult("output_failed"); stop.Cancel(); }
        }
        var stdout = Drain(process.StandardOutput, ProcessOutputStream.StandardOutput);
        var stderr = Drain(process.StandardError, ProcessOutputStream.StandardError);
        var exit = process.WaitForExitAsync(CancellationToken.None);
        var all = Task.WhenAll(exit, stdout, stderr);
        var interrupted = Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        await Task.WhenAny(all, interrupted).ConfigureAwait(false);
        var completion = ProcessCompletion.Exited;
        var terminationFailed = false;
        if (stop.IsCancellationRequested)
        {
            completion = cancellationToken.IsCancellationRequested ? ProcessCompletion.Cancelled : ProcessCompletion.TimedOut;
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // Already exited, including the race immediately before Kill.
            catch { terminationFailed = true; }
        }
        try
        {
            // On an OS termination error, do not wait forever for a still-running child.
            if (terminationFailed)
                await all.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            else await all.ConfigureAwait(false);
        }
        catch when (terminationFailed) { throw new ProcessRunnerException("termination_failed"); }
        catch { throw new ProcessRunnerException("process_failed"); }
        finally { callbackLock.Dispose(); }
        if (terminationFailed) throw new ProcessRunnerException("termination_failed");
        if (failure.Task.IsCompletedSuccessfully) throw new ProcessRunnerException(failure.Task.Result);
        return new(pid, process.ExitCode, completion, truncated != 0);
    }

    private static string ValidateDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("invalid_workspace");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var directory = new DirectoryInfo(full);
        if (!directory.Exists) throw new ArgumentException("invalid_workspace");
        // Reject links/junctions in every component, not merely a lexical prefix check.
        for (var current = directory; current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("invalid_workspace");
        return full;
    }

    private sealed class SecretFilter(string[] secrets)
    {
        private readonly StringBuilder pending = new();
        public string Push(string input, bool final)
        {
            var result = new StringBuilder();
            foreach (var ch in input) { pending.Append(ch); Flush(false, result); }
            if (final) Flush(true, result);
            return result.ToString();
        }
        private void Flush(bool final, StringBuilder result)
        {
            while (pending.Length > 0)
            {
                var text = pending.ToString();
                if (!final && secrets.Any(s => s.StartsWith(text, StringComparison.Ordinal))) return;
                var match = secrets.FirstOrDefault(s => text.StartsWith(s, StringComparison.Ordinal));
                if (match is not null) { result.Append("[REDACTED]"); pending.Remove(0, match.Length); }
                else { result.Append(pending[0]); pending.Remove(0, 1); }
            }
        }
    }
}