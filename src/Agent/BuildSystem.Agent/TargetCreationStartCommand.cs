namespace BuildSystem.Agent;

public sealed class TargetCreationStartCommandOptions
{
    // The future trusted host must opt in explicitly; no worker/default command enables this.
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationStartCommandOptions [NONEXECUTABLE]";
}

/// <summary>
/// Explicit, known-permit, one-shot NONCREATE metadata command. Never adopts local state,
/// resolves credentials, creates a target, or unblocks scheduled jobs. Does not own dependencies.
/// The trusted owner must serialize the entire invocation with all other journal users, including
/// other command instances: the coordinator's gate alone does not protect the prepared read.
/// </summary>
public sealed class TargetCreationStartCommand
{
    private readonly AgentJournal _journal;
    private readonly ITargetCreationPreparedPermitTransport _prepared;
    private readonly ITargetCreationPermitStartTransport _starts;
    private readonly bool _enabled;

    public TargetCreationStartCommand(AgentJournal journal, ITargetCreationPreparedPermitTransport prepared,
        ITargetCreationPermitStartTransport starts, TargetCreationStartCommandOptions options)
    {
        _journal = journal;
        _prepared = prepared;
        _starts = starts;
        _enabled = options?.Enabled == true;
    }

    public static bool SupportedArguments(string[] args) => TryParse(args, out _);
    public static bool IsStartCommand(string[] args) => TryParse(args, out _);

    public static bool TryParse(string[] args, out Guid permitId)
    {
        permitId = Guid.Empty;
        if (args is not { Length: 2 } || args[0] != "record-target-creation-start" ||
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
        // Disabled wins before inspecting cancellation, caller pins, journal or transports.
        if (!_enabled) return await WriteResultAsync(output, "TargetCreationStartDisabled", 3).ConfigureAwait(false);

        string status;
        int exitCode;
        try
        {
            ct.ThrowIfCancellationRequested();
            // An explicitly addressed permit must match the caller's existing binding before I/O.
            if (permitId == Guid.Empty || expectedIntent is null || expectedBinding is null ||
                permitId != expectedBinding.PermitId || expectedIntent.OperationId == Guid.Empty ||
                expectedIntent.OperationId != expectedBinding.OperationId ||
                _journal is null || _prepared is null || _starts is null)
                throw new TargetCreationStartCoordinationException();

            var local = _journal.GetTargetCreationIntent(expectedIntent.OperationId);
            var binding = _journal.GetTargetCreationPermitBinding(expectedIntent.OperationId);
            if (local is null || local.Intent != expectedIntent || binding is null || binding != expectedBinding)
                throw new TargetCreationStartCoordinationException();

            if (local.State != TargetCreationState.IntentRecorded)
            {
                // A prior attempt is not a reason to GET, POST, reconcile, reset or retry.
                status = "TargetCreationStartAlreadyAttempted";
                exitCode = 2;
            }
            else
            {
                // Validate the actual persisted initial record, never a synthetic/adopted slot.
                _ = TargetCreationLocalPreflight.ValidateInitial(local, binding);
                ct.ThrowIfCancellationRequested();
                var prepared = await _prepared.GetPreparedAsync(expectedIntent, binding, ct).ConfigureAwait(false);
                if (prepared is null || prepared.TargetIntent != expectedIntent || prepared.ScopeBinding != binding ||
                    prepared.Executable || prepared.FreshAuthority || !prepared.ScheduledJobsMustRemainBlocked ||
                    prepared.RetryAllowed)
                    throw new TargetCreationStartCoordinationException();
                ct.ThrowIfCancellationRequested();

                // Only the coordinator may durably begin; its local begin precedes its sole POST.
                // A failed prepared read above has made no journal mutation.
                var coordinator = new TargetCreationStartCoordinator(_journal, _starts,
                    new TargetCreationStartCoordinatorOptions { Enabled = _enabled });
                var receipt = await coordinator.RecordStartAsync(expectedIntent, binding, ct).ConfigureAwait(false);
                (status, exitCode) = receipt.Outcome switch
                {
                    TargetCreationStartCoordinationOutcome.StartMetadataRecorded => ("StartMetadataRecorded", 0),
                    TargetCreationStartCoordinationOutcome.AlreadyAttempted => ("TargetCreationStartAlreadyAttempted", 2),
                    _ => ("TargetCreationStartReconciliationRequired", 4)
                };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "TargetCreationStartCancelled";
            exitCode = 130;
        }
        catch (TargetCreationStartCoordinationException e) when (e.Code == "TargetCreationStartReconciliationUnconfirmed")
        {
            status = "TargetCreationStartReconciliationRequired";
            exitCode = 4;
        }
        catch (Exception)
        {
            // Neither exception text nor caller/server data is diagnostic output.
            status = "TargetCreationStartUnavailable";
            exitCode = 5;
        }

        return await WriteResultAsync(output, status, exitCode).ConfigureAwait(false);
    }

    private static async Task<int> WriteResultAsync(TextWriter output, string status, int exitCode)
    {
        try
        {
            // All statuses are fixed literals. Output is attempted once, outside operation catches:
            // an output failure after begin cannot roll back, reset, retry, or emit a second line.
            await output.WriteLineAsync("{\"status\":\"" + status +
                "\",\"executable\":false,\"scheduledJobsMustRemainBlocked\":true,\"retryAllowed\":false}")
                .ConfigureAwait(false);
            return exitCode;
        }
        catch (Exception) { return 5; }
    }
}
