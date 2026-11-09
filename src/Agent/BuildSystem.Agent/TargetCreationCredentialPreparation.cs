using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildSystem.Agent;

public sealed class TargetCreationCredentialPreparationOptions { public bool Enabled { get; init; } }
public enum TargetCreationCredentialPreparationOutcome { CredentialsPrepared, AlreadyAttempted, ReconciliationRequired }
public sealed class TargetCreationCredentialPreparationException : Exception
{
    internal TargetCreationCredentialPreparationException(bool unconfirmed = false) : base(unconfirmed
        ? "TargetCreationCredentialPreparationReconciliationUnconfirmed" : "TargetCreationCredentialPreparationUnavailable") { }
    public string Code => Message;
    public override string ToString() => Code;
}

/// <summary>Owned ephemeral delivery, never permission to invoke CREATE. Escaped managed strings cannot be erased.</summary>
[JsonConverter(typeof(TargetCreationCredentialPreparationReceiptJsonConverter))]
public sealed class TargetCreationCredentialPreparationReceipt : IDisposable
{
    private TargetCreationDeliveredCredentials? _credentials;
    internal TargetCreationCredentialPreparationReceipt(TargetCreationRecord record, TargetCreationPermitBinding binding,
        TargetCreationCredentialPreparationOutcome outcome, TargetCreationDeliveredCredentials? credentials = null)
    { Record = record; Binding = binding; Outcome = outcome; _credentials = credentials; }
    public TargetCreationRecord Record { get; }
    public TargetCreationPermitBinding Binding { get; }
    public TargetCreationCredentialPreparationOutcome Outcome { get; }
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool RetryAllowed => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public TargetCreationDeliveredSecretValues ReadSecrets() => Volatile.Read(ref _credentials)?.ReadSecrets()
        ?? throw new ObjectDisposedException(nameof(TargetCreationCredentialPreparationReceipt), "Credentials unavailable.");
    public void Dispose() => Interlocked.Exchange(ref _credentials, null)?.Dispose();
    public override string ToString() => "TargetCreationCredentialPreparationReceipt [REDACTED; NONEXECUTABLE]";
}

public sealed class TargetCreationCredentialPreparationReceiptJsonConverter : JsonConverter<TargetCreationCredentialPreparationReceipt>
{
    public override bool HandleNull => true;
    public override TargetCreationCredentialPreparationReceipt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new JsonException("Credential preparation receipt serialization forbidden.");
    public override void Write(Utf8JsonWriter writer, TargetCreationCredentialPreparationReceipt value, JsonSerializerOptions options)
        => throw new JsonException("Credential preparation receipt serialization forbidden.");
}

/// <summary>Alternative initial orchestration, not a continuation after metadata CLI consumption.
/// Trusted owner serializes all other journal access. No worker, output, physical calls or authority.</summary>
public sealed class TargetCreationCredentialPreparation
{
    private readonly AgentJournal _journal;
    private readonly ITargetCreationCredentialTransport _credentials;
    private readonly TargetCreationStartCoordinator _starts;
    private readonly bool _enabled;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public TargetCreationCredentialPreparation(AgentJournal journal, ITargetCreationPermitStartTransport starts,
        ITargetCreationCredentialTransport credentials, TargetCreationCredentialPreparationOptions options)
    {
        if (journal is null || starts is null || credentials is null || options is null)
            throw new TargetCreationCredentialPreparationException();
        _journal = journal; _credentials = credentials; _enabled = options.Enabled;
        _starts = new(journal, starts, new() { Enabled = _enabled });
    }
    public async Task<TargetCreationCredentialPreparationReceipt> PrepareAsync(TargetCreationIntent expectedIntent,
        TargetCreationPermitBinding expectedBinding, CancellationToken cancellationToken = default)
    {
        if (!_enabled) throw new TargetCreationCredentialPreparationException();
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TargetCreationStartCoordinationReceipt start;
            try { start = await _starts.RecordStartAsync(expectedIntent, expectedBinding, cancellationToken).ConfigureAwait(false); }
            catch (TargetCreationStartCoordinationException e)
            { throw new TargetCreationCredentialPreparationException(e.Code == "TargetCreationStartReconciliationUnconfirmed"); }
            if (start.Outcome != TargetCreationStartCoordinationOutcome.StartMetadataRecorded)
                return new(start.Record, start.Binding, start.Outcome == TargetCreationStartCoordinationOutcome.AlreadyAttempted
                    ? TargetCreationCredentialPreparationOutcome.AlreadyAttempted : TargetCreationCredentialPreparationOutcome.ReconciliationRequired);
            TargetCreationDeliveredCredentials? holder = null;
            try
            {
                _ = TargetCreationLocalPreflight.ValidateInFlight(start.Record, expectedBinding);
                if (start.Record.Intent != expectedIntent || start.Binding != expectedBinding) throw new InvalidOperationException();
                cancellationToken.ThrowIfCancellationRequested();
                holder = await _credentials.ResolveAsync(new(expectedBinding.PermitId, 2, expectedBinding.AttemptId,
                    expectedBinding.CredentialId!.Value, expectedBinding.EndpointMappingId!.Value), cancellationToken).ConfigureAwait(false);
                if (holder is null || holder.Executable || holder.RetryAllowed || !holder.ScheduledJobsMustRemainBlocked)
                    throw new InvalidOperationException();
                // Detect disposed/invalid trusted adapter output. Transient managed references are not erasable.
                ValidateSecrets(holder.ReadSecrets());
                cancellationToken.ThrowIfCancellationRequested();
                var receipt = new TargetCreationCredentialPreparationReceipt(start.Record, start.Binding,
                    TargetCreationCredentialPreparationOutcome.CredentialsPrepared, holder);
                holder = null; // Ownership transfers once, never a plaintext cache or execution grant.
                return receipt;
            }
            catch (Exception)
            {
                holder?.Dispose(); holder = null;
                try
                {
                    var record = _journal.QuarantineTargetCreation(expectedIntent.OperationId, 2,
                        cancellationToken.IsCancellationRequested ? "Cancelled" : "OutcomeUnknown");
                    return new(record, expectedBinding, TargetCreationCredentialPreparationOutcome.ReconciliationRequired);
                }
                catch (Exception) { throw new TargetCreationCredentialPreparationException(true); }
            }
            finally { holder?.Dispose(); }
        }
        catch (TargetCreationCredentialPreparationException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) { throw new TargetCreationCredentialPreparationException(); }
        finally { _gate.Release(); }
    }
    private static void ValidateSecrets(TargetCreationDeliveredSecretValues values)
    {
        foreach (var value in new[] { values.ClusterUser, values.ClusterPassword, values.SqlUser, values.SqlPassword })
        {
            if (string.IsNullOrEmpty(value) || value.Length > 4096 || value.Any(char.IsControl)) throw new InvalidOperationException();
            for (var i = 0; i < value.Length; i++)
                if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i])))
                    throw new InvalidOperationException();
        }
    }
}