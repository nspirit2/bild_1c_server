namespace BuildSystem.Agent;

public sealed class TargetCreationStartCoordinatorOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationStartCoordinatorOptions [NONEXECUTABLE]";
}

public enum TargetCreationStartCoordinationOutcome
{
    StartMetadataRecorded,
    ReconciliationRequired,
    AlreadyAttempted
}

/// <summary>Local unresolved metadata, never execution authority or proof of external ownership.</summary>
public sealed record TargetCreationStartCoordinationReceipt(TargetCreationRecord Record,
    TargetCreationPermitBinding Binding, TargetCreationStartCoordinationOutcome Outcome,
    TargetCreationPermitStartReceipt? StartReceipt = null)
{
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool RetryAllowed => false;
    public override string ToString() => "TargetCreationStartCoordinationReceipt [NONEXECUTABLE]";
}

public sealed class TargetCreationStartCoordinationException : Exception
{
    public TargetCreationStartCoordinationException() : this(false) { }
    internal TargetCreationStartCoordinationException(bool reconciliationUnconfirmed)
        : base(reconciliationUnconfirmed ? "TargetCreationStartReconciliationUnconfirmed" : "TargetCreationStartUnavailable") { }
    public string Code => Message;
    public override string ToString() => Code;
}

/// <summary>Opt-in, one-attempt metadata handshake. Does not own the journal or transport, resolve
/// credentials, create physical targets, or release local holds. The trusted owner must serialize
/// other access to the journal: this gate protects only calls through this coordinator instance.</summary>
public sealed class TargetCreationStartCoordinator
{
    private readonly AgentJournal _journal;
    private readonly ITargetCreationPermitStartTransport _transport;
    private readonly bool _enabled;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TargetCreationStartCoordinator(AgentJournal journal, ITargetCreationPermitStartTransport transport,
        TargetCreationStartCoordinatorOptions options)
    {
        if (journal is null || transport is null || options is null)
            throw new TargetCreationStartCoordinationException();
        _journal = journal;
        _transport = transport;
        _enabled = options.Enabled;
    }

    public async Task<TargetCreationStartCoordinationReceipt> RecordStartAsync(TargetCreationIntent expectedIntent,
        TargetCreationPermitBinding expectedBinding, CancellationToken cancellationToken = default)
    {
        // Disabled is the first gate, including for canceled or malformed calls.
        if (!_enabled) throw new TargetCreationStartCoordinationException();
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Caller shape is not evidence that these pins were persisted locally.
            _ = TargetCreationLocalPreflight.ValidateInitial(
                new(expectedIntent, TargetCreationState.IntentRecorded, 1, null, null, null), expectedBinding);
            var local = _journal.GetTargetCreationIntent(expectedIntent.OperationId);
            var binding = _journal.GetTargetCreationPermitBinding(expectedIntent.OperationId);
            if (local is null || local.Intent != expectedIntent || binding is null || binding != expectedBinding)
                throw new TargetCreationStartCoordinationException();
            if (local.State != TargetCreationState.IntentRecorded)
                return new(local, binding, TargetCreationStartCoordinationOutcome.AlreadyAttempted);
            _ = TargetCreationLocalPreflight.ValidateInitial(local, binding);
            cancellationToken.ThrowIfCancellationRequested();

            // Durable local consumption precedes the sole possible POST. If commit throws, its
            // outcome is unknown: never POST, reread/reenter, or quarantine someone else's attempt.
            TargetCreationAttemptReceipt attempt;
            try { attempt = _journal.TryBeginTargetCreation(expectedIntent.OperationId, 1, binding.AttemptId); }
            catch (Exception) { throw new TargetCreationStartCoordinationException(); }
            if (!attempt.NewlyRecorded)
                return new(attempt.Record, binding, TargetCreationStartCoordinationOutcome.AlreadyAttempted);

            TargetCreationPermitStartReceipt start;
            try
            {
                if (attempt.Record.Intent != expectedIntent)
                    throw new TargetCreationStartCoordinationException();
                _ = TargetCreationLocalPreflight.ValidateInFlight(attempt.Record, binding);
                // Cancellation after durable consumption must quarantine, never escape as OCE.
                cancellationToken.ThrowIfCancellationRequested();
                start = await _transport.RecordStartAsync(expectedIntent, binding, cancellationToken).ConfigureAwait(false);
                ValidateStart(start, expectedIntent, binding);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception)
            {
                return Quarantine(attempt.Record, binding, cancellationToken.IsCancellationRequested);
            }

            // Server replay (NewlyRecorded=false) is equally valid inert metadata. Neither success
            // nor replay settles the local in-flight attempt or permits execution or another POST.
            return new(attempt.Record, binding, TargetCreationStartCoordinationOutcome.StartMetadataRecorded, start);
        }
        catch (TargetCreationStartCoordinationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) { throw new TargetCreationStartCoordinationException(); }
        finally { _gate.Release(); }
    }

    private TargetCreationStartCoordinationReceipt Quarantine(TargetCreationRecord begun,
        TargetCreationPermitBinding binding, bool cancelled)
    {
        try
        {
            // No reread/retry: revision 2 is the exact attempt consumed by this call. Journal CAS
            // failure (including an uncertain commit) is not confirmation of reconciliation.
            var record = _journal.QuarantineTargetCreation(begun.Intent.OperationId, 2,
                cancelled ? "Cancelled" : "OutcomeUnknown");
            return new(record, binding, TargetCreationStartCoordinationOutcome.ReconciliationRequired);
        }
        catch (Exception) { throw new TargetCreationStartCoordinationException(true); }
    }

    private static void ValidateStart(TargetCreationPermitStartReceipt? receipt, TargetCreationIntent intent,
        TargetCreationPermitBinding binding)
    {
        // Independently correlate even a trusted transport's result. No public evidence/authority
        // assertion is inferred from this historical receipt or its outer NewlyRecorded flag.
        if (receipt is null || receipt.TargetIntent != intent || receipt.ScopeBinding != binding ||
            receipt.PermitId != binding.PermitId || receipt.OperationId != intent.OperationId ||
            receipt.AttemptId != binding.AttemptId || receipt.Revision != 2 ||
            receipt.StartedAt.Offset != TimeSpan.Zero || receipt.StartedAt < binding.IssuedAt ||
            receipt.StartedAt >= binding.StartBefore || receipt.Executable || receipt.FreshAuthority ||
            !receipt.ScheduledJobsMustRemainBlocked || receipt.RetryAllowed)
            throw new TargetCreationStartCoordinationException();
    }
}
