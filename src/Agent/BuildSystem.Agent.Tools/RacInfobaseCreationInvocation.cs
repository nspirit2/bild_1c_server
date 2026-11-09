using System.Text;

namespace BuildSystem.Agent.Tools;

/// <summary>
/// Explicit operator opt-in for the isolated invocation adapter. Disabled by default;
/// enabling this option is not an execution permit and does not register a worker.
/// </summary>
public sealed class RacInfobaseCreationInvocationOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "RacInfobaseCreationInvocationOptions [details omitted]";
}

/// <summary>A fixed-code error without invocation, credential, or runner details.</summary>
public sealed class RacInfobaseCreationInvocationException : Exception
{
    internal RacInfobaseCreationInvocationException(string code) : base(code) => Code = code;
    public string Code { get; }
    public override string ToString() => "RacInfobaseCreationInvocationException: " + Code;
}

/// <summary>
/// Default-off, lower-level adapter for exactly one factory-validated creation recipe.
/// When explicitly enabled this can launch a real process through the runner; it is not a dry run.
/// This adapter is NOT an authorization boundary and is not registered as a worker.
/// Before calling it, the caller must independently obtain a fresh authoritative online
/// permit, verify immutable credential and target pins and the local journal, and hold
/// the exclusive namespace while verifying fresh absence in the same critical section.
/// An inert recipe is never a grant: its always-false Executable metadata is neither
/// promoted nor interpreted as authorization. Its command is consumed only as argv.
/// Even an acknowledgement requires independent reconciliation before any use or unblock.
/// The injected runner must enforce trusted tools, no shell, redaction, and process bounds.
/// </summary>
public sealed class RacInfobaseCreationInvocation
{
    private readonly IProcessRunner runner;
    private readonly bool enabled;

    public RacInfobaseCreationInvocation(IProcessRunner runner, RacInfobaseCreationInvocationOptions options)
    {
        if (runner is null || options is null)
            throw new RacInfobaseCreationInvocationException("InvalidRacInfobaseCreationInvocationOptions");
        this.runner = runner;
        enabled = options.Enabled;
    }

    public async Task<RacInfobaseCreationOutcome> RunAsync(RacInfobaseCreationCommandRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        // Gate before touching invocation data, arguments, secrets, or a callback.
        if (!enabled)
            throw new RacInfobaseCreationInvocationException("RacInfobaseCreationInvocationDisabled");
        if (recipe is null)
            throw new RacInfobaseCreationInvocationException("InvalidRacInfobaseCreationRecipe");

        // Recipes are constructible only by the validated factory within this assembly.
        // Reuse the classifier's fixed-code scope validation without inspecting the command.
        var unknown = RacInfobaseCreationOutcomes.Classify(recipe.Scope, null, null, null);
        cancellationToken.ThrowIfCancellationRequested();

        const int limit = RacInfobaseCreationOutcomes.MaxOutputCharacters;
        var stdout = new StringBuilder(limit, limit);
        var stderr = new StringBuilder(limit, limit);
        var gate = new object();
        var captureFailed = false;
        var frozen = false;

        Task Capture(ProcessOutput item)
        {
            lock (gate)
            {
                if (frozen)
                    return Task.CompletedTask;
                if (item is null || item.Text is null ||
                    item.Stream is not (ProcessOutputStream.StandardOutput or ProcessOutputStream.StandardError))
                {
                    captureFailed = true;
                    return Task.CompletedTask;
                }

                if (captureFailed)
                    return Task.CompletedTask;
                try
                {
                    var remaining = limit - stdout.Length - stderr.Length;
                    var count = Math.Min(remaining, item.Text.Length);
                    if (count != item.Text.Length)
                        captureFailed = true;
                    var target = item.Stream == ProcessOutputStream.StandardOutput ? stdout : stderr;
                    // ProcessRunner supplies raw fragments (including existing LF), not logical
                    // lines: adding a newline would corrupt valid line-limit split output.
                    target.Append(item.Text.AsSpan(0, count));
                }
                catch
                {
                    // Never interrupt draining, even if capture itself fails.
                    captureFailed = true;
                }
            }
            return Task.CompletedTask;
        }

        try
        {
            // Keep pre-invocation cancellation outside the runner exception boundary.
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult? result;
            try
            {
                // Exactly one call; no retries and no separate process/shell construction.
                result = await runner.RunAsync(recipe.Command, Capture, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Invocation may have happened, including when the runner throws cancellation.
                // Never propagate its diagnostics, command, output, or exception details.
                lock (gate)
                {
                    frozen = true;
                }
                return unknown;
            }

            lock (gate)
            {
                // ProcessRunner normally awaits callbacks before returning. Cancellation can
                // leave a callback late; freeze under the same lock and ignore such callbacks.
                frozen = true;
                return RacInfobaseCreationOutcomes.Classify(recipe.Scope, result,
                    stdout.ToString(), stderr.ToString(), captureFailed,
                    cancellationToken.IsCancellationRequested);
            }
        }
        finally
        {
            lock (gate)
            {
                frozen = true;
                // Best-effort lifetime reduction only: managed strings cannot be securely erased.
                try { stdout.Clear(); } catch { }
                try { stderr.Clear(); } catch { }
            }
        }
    }

    public override string ToString() => "RacInfobaseCreationInvocation [details omitted]";
}
