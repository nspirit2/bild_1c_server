namespace BuildSystem.Agent;

public sealed class TargetCreationOneShotCommandOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationOneShotCommandOptions [NONEXECUTABLE]";
}

/// <summary>
/// Dormant known-permit command; never registers a host, retries, or reports creation success.
/// Does not own dependencies. The trusted owner must serialize the entire invocation against
/// every other journal user; the coordinator gate does not protect the prepared read.
/// </summary>
public sealed class TargetCreationOneShotCommand
{
    private readonly AgentJournal? _journal;
    private readonly ITargetCreationPreparedPermitTransport? _prepared;
    private readonly TargetCreationOneShotCoordinator? _coordinator;
    private readonly bool _enabled;

    public TargetCreationOneShotCommand(AgentJournal? journal, ITargetCreationPreparedPermitTransport? prepared,
        TargetCreationOneShotCoordinator? coordinator, TargetCreationOneShotCommandOptions? options)
    {
        _journal = journal;
        _prepared = prepared;
        _coordinator = coordinator;
        _enabled = options?.Enabled == true;
    }

    public static bool SupportedArguments(string[] args) => TryParse(args, out _);
    public static bool IsOneShotCommand(string[] args) => TryParse(args, out _);

    public static bool TryParse(string[] args, out Guid permitId)
    {
        permitId = Guid.Empty;
        if (args is not { Length: 2 } || args[0] != "run-target-creation-once" ||
            args[1] is not { Length: 36 } || !Guid.TryParseExact(args[1], "D", out var parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(args[1], parsed.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return false;
        permitId = parsed;
        return true;
    }

    public async Task<int> RunAsync(Guid permitId, TargetCreationIntent expectedIntent,
        TargetCreationPermitBinding expectedBinding, TextWriter output, CancellationToken ct = default)
    {
        // Disabled wins before cancellation, caller pins, dependency checks, or any journal access.
        if (!_enabled) return await WriteResultAsync(output, "TargetCreationOneShotDisabled", 3, false, false).ConfigureAwait(false);

        string status;
        int exitCode;
        var physicalInvocationAttempted = false;
        var creationAcknowledged = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (permitId == Guid.Empty || expectedIntent is null || expectedBinding is null ||
                permitId != expectedBinding.PermitId || expectedIntent.OperationId == Guid.Empty ||
                expectedIntent.OperationId != expectedBinding.OperationId ||
                _journal is null || _prepared is null || _coordinator is null)
                throw new InvalidOperationException();

            var local = _journal.GetTargetCreationIntent(expectedIntent.OperationId);
            var binding = _journal.GetTargetCreationPermitBinding(expectedIntent.OperationId);
            if (local is null || local.Intent != expectedIntent || binding is null || binding != expectedBinding)
                throw new InvalidOperationException();

            if (local.State != TargetCreationState.IntentRecorded)
            {
                // A previous attempt never triggers a read, coordinator entry or extra quarantine.
                status = "TargetCreationOneShotAlreadyAttempted";
                exitCode = 2;
            }
            else
            {
                _ = TargetCreationLocalPreflight.ValidateInitial(local, binding);
                ct.ThrowIfCancellationRequested();
                var prepared = await _prepared.GetPreparedAsync(expectedIntent, binding, ct).ConfigureAwait(false);
                if (prepared is null || prepared.TargetIntent != expectedIntent || prepared.ScopeBinding != binding ||
                    prepared.Executable || prepared.FreshAuthority || !prepared.ScheduledJobsMustRemainBlocked ||
                    prepared.RetryAllowed)
                    throw new InvalidOperationException();
                ct.ThrowIfCancellationRequested();

                // Only the coordinator begins and quarantines; it completes secret cleanup before returning.
                var receipt = await _coordinator.RunAsync(expectedIntent, binding, ct).ConfigureAwait(false);
                if (receipt is null || receipt.Record is null || receipt.Record.Intent != expectedIntent ||
                    receipt.Binding != binding || receipt.Executable || receipt.FreshAuthority ||
                    !receipt.ScheduledJobsMustRemainBlocked || receipt.RetryAllowed)
                    throw new InvalidOperationException();
                physicalInvocationAttempted = receipt.PhysicalInvocationAttempted;
                creationAcknowledged = receipt.CreationAcknowledged;
                (status, exitCode) = receipt.Outcome == TargetCreationOneShotOutcome.AlreadyAttempted
                    ? ("TargetCreationOneShotAlreadyAttempted", 2)
                    : ("TargetCreationOneShotReconciliationRequired", 4);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "TargetCreationOneShotCancelled";
            exitCode = 130;
        }
        catch (TargetCreationOneShotException e) when (e.Code == "TargetCreationOneShotReconciliationUnconfirmed")
        {
            status = "TargetCreationOneShotReconciliationUnconfirmed";
            exitCode = 6;
        }
        catch (Exception)
        {
            status = "TargetCreationOneShotUnavailable";
            exitCode = 5;
        }

        return await WriteResultAsync(output, status, exitCode, physicalInvocationAttempted, creationAcknowledged).ConfigureAwait(false);
    }

    private static async Task<int> WriteResultAsync(TextWriter output, string status, int exitCode,
        bool physicalInvocationAttempted, bool creationAcknowledged)
    {
        try
        {
            // Fixed closed JSON, attempted once outside operation catches; never reenter or mutate on failure.
            await output.WriteLineAsync("{\"status\":\"" + status +
                "\",\"executable\":false,\"freshAuthority\":false,\"scheduledJobsMustRemainBlocked\":true,\"retryAllowed\":false" +
                ",\"physicalInvocationAttempted\":" + (physicalInvocationAttempted ? "true" : "false") +
                ",\"creationAcknowledged\":" + (creationAcknowledged ? "true" : "false") + "}").ConfigureAwait(false);
            return exitCode;
        }
        catch (Exception) { return 5; }
    }
}
