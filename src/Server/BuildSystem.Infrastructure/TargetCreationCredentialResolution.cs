using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using BuildSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Default-off infrastructure-only resolution. Not an execution or secret-delivery opt-in.</summary>
public sealed class TargetCreationCredentialResolutionOptions
{
    public bool Enabled { get; init; }
    public override string ToString() => "TargetCreationCredentialResolutionOptions [NONEXECUTABLE]";
}

/// <summary>
/// Short-lived internal plaintext holder, deliberately not a response DTO or execution grant.
/// Dispose drops this holder's reference only: immutable managed strings, including values already
/// returned by ReadSecrets, cannot be reliably erased. Never log, serialize or retain the secrets.
/// </summary>
[JsonConverter(typeof(TargetCreationResolvedCredentialsJsonConverter))]
public sealed class TargetCreationResolvedCredentials : IDisposable
{
    private TargetCredentialSecrets? secrets;

    internal TargetCreationResolvedCredentials(TargetCredentialSecrets secrets) => this.secrets = secrets;

    public bool Executable => false;
    /// <summary>No permission to retry physical creation; fresh internal resolution remains repeatable.</summary>
    public bool RetryAllowed => false;

    // Methods are not JSON value members. The deny-all converter is an additional mandatory barrier.
    public TargetCredentialSecrets ReadSecrets() => Volatile.Read(ref secrets)
        ?? throw new ObjectDisposedException(nameof(TargetCreationResolvedCredentials));

    public void Dispose() => Interlocked.Exchange(ref secrets, null);
    public override string ToString() => "TargetCreationResolvedCredentials [REDACTED; NONEXECUTABLE]";
}

/// <summary>Rejects both serialization and deserialization, without including any supplied values.</summary>
public sealed class TargetCreationResolvedCredentialsJsonConverter : JsonConverter<TargetCreationResolvedCredentials>
{
    public override bool HandleNull => true;

    public override TargetCreationResolvedCredentials Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => throw new JsonException("Target credentials unavailable.");

    public override void Write(Utf8JsonWriter writer, TargetCreationResolvedCredentials value,
        JsonSerializerOptions options) => throw new JsonException("Target credentials unavailable.");
}

/// <summary>
/// Scoped SQL-only credential resolution foundation. Nothing executes, consumes a permit or delivers
/// secrets to an external callback. Repeated internal resolution requires every current check again;
/// no exactly-once delivery policy is implied. Plaintext never enters entities, audit or diagnostics.
/// </summary>
public interface ITargetCreationCredentialResolver
{
    Task<TargetCreationResolvedCredentials> ResolveAsync(string token, Guid permitId,
        long expectedPermitRevision, Guid attemptId, Guid credentialId, Guid endpointMappingId, CancellationToken ct);
}

public sealed class SqlTargetCreationCredentialResolver(BuildSystemDbContext db,
    TargetCreationPermitOptions permits, TargetCreationOperationOptions creation,
    ITargetCredentialProtector protector, TargetCredentialOptions credentialOptions,
    TargetCreationCredentialResolutionOptions options) : ITargetCreationCredentialResolver
{
    private readonly SqlTargetCreationOperations ledger = new(db, creation);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<TargetCreationResolvedCredentials> ResolveAsync(string token, Guid permitId,
        long expectedPermitRevision, Guid attemptId, Guid credentialId, Guid endpointMappingId, CancellationToken ct)
    {
        // All gates and request-shape checks precede SQL, including the independently disabled store.
        if (!options.Enabled || !permits.Enabled || !permits.AgentStartEnabled ||
            !permits.CredentialBindingEnabled || !creation.Enabled || !credentialOptions.Enabled)
            throw Reject(409, "TargetCreationCredentialResolutionDisabled");
        if (permitId == Guid.Empty || attemptId == Guid.Empty || credentialId == Guid.Empty ||
            endpointMappingId == Guid.Empty || expectedPermitRevision != 2)
            throw Reject(400, "InvalidTargetCreationCredentialResolution");

        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                TargetCreationResolvedCredentials? result = null;
                try
                {
                    db.ChangeTracker.Clear();
                    await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                    await ledger.Lock("BuildSystem:agent-control", ct);
                    // Authentication is repeated under the global lock on every strategy attempt,
                    // before even looking up whether a permit exists.
                    if (string.IsNullOrEmpty(token) || token.Length > 256)
                        throw Reject(401, "AgentUnauthorized");
                    var hash = TokenProtector.Hash(token);
                    var agent = await db.Agents.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.TokenHash == hash && x.State == "Approved", ct);
                    if (agent is null || !TokenProtector.Verify(token, agent.TokenHash!))
                        throw Reject(401, "AgentUnauthorized");

                    var permit = await db.TargetCreationPermits.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.PermitId == permitId, ct) ?? throw Unavailable();
                    var operation = await db.TargetCreationOperations.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.OperationId == permit.OperationId, ct) ?? throw Unavailable();
                    var intent = await db.RestoreIntents.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.RestoreIntentId == operation.RestoreIntentId, ct) ?? throw Unavailable();
                    if (agent.Id != intent.AgentId) throw Reject(403, "TargetCreationPermitForbidden");
                    await ledger.Admission(intent, ct);
                    _ = SqlTargetCreationPermits.Receipt(permit, operation, intent, false);

                    var now = await db.Database.SqlQuery<DateTimeOffset>(
                        $"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
                    // Validate the complete started permit shape, not just its state or request pins.
                    // The upper bound precedes AddMinutes so even corrupt timestamps cannot overflow.
                    if (permit.PermitId != permitId || permit.OperationId == Guid.Empty ||
                        permit.OperationId != operation.OperationId || permit.AttemptId == Guid.Empty ||
                        permit.AttemptId != attemptId || permit.ExpectedOperationRevision != 1 ||
                        permit.Executable || !permit.ScheduledJobsMustRemainBlocked ||
                        permit.CredentialId != credentialId || permit.EndpointMappingId != endpointMappingId ||
                        permit.State != TargetCreationPermitState.StartRecorded || permit.Revision != expectedPermitRevision ||
                        permit.StartExpectedRevision != 1 || permit.StartedAt is not { } started ||
                        permit.IssuedAt.Offset != TimeSpan.Zero || permit.StartBefore.Offset != TimeSpan.Zero ||
                        started.Offset != TimeSpan.Zero || now.Offset != TimeSpan.Zero ||
                        permit.IssuedAt > DateTimeOffset.MaxValue.AddMinutes(-5) ||
                        permit.StartBefore != permit.IssuedAt.AddMinutes(5) ||
                        started < permit.IssuedAt || started >= permit.StartBefore || started > now || now >= permit.StartBefore ||
                        operation.State != TargetCreationOperationState.AttemptRecorded || operation.Revision != 2 ||
                        operation.AttemptId != attemptId || operation.AttemptExpectedRevision != 1)
                        throw Unavailable();

                    // This is the first query that can materialize an encrypted envelope. All current
                    // authentication, admission, exact pin, revision and deadline checks precede it.
                    var credential = await db.TargetCredentials.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.CredentialId == credentialId, ct);
                    if (credential is null || credential.CredentialId != credentialId ||
                        credential.CanonicalClusterId == Guid.Empty || credential.CanonicalSqlServerId == Guid.Empty ||
                        credential.CanonicalClusterId != intent.CanonicalClusterId ||
                        credential.CanonicalSqlServerId != intent.CanonicalSqlServerId ||
                        credential.EndpointMappingId != endpointMappingId ||
                        credential.State != TargetCredentialState.Stored || credential.Revision != 1 ||
                        credential.RevokedAt is not null || credential.CreatedAt.Offset != TimeSpan.Zero ||
                        credential.CreatedAt > now || string.IsNullOrWhiteSpace(credential.Ciphertext) ||
                        credential.Ciphertext.Length > TargetCredentialStorage.MaxCiphertextCharacters)
                        throw Unavailable();

                    try
                    {
                        var secrets = protector.Unprotect(new TargetCredentialScope(credentialId,
                            intent.CanonicalClusterId, intent.CanonicalSqlServerId, endpointMappingId), credential.Ciphertext);
                        if (secrets is null || !ValidField(secrets.ClusterUser) || !ValidField(secrets.ClusterPassword) ||
                            !ValidField(secrets.SqlUser) || !ValidField(secrets.SqlPassword))
                            throw Unavailable();
                        result = new TargetCreationResolvedCredentials(secrets);
                    }
                    catch (Exception)
                    {
                        // Trusted providers can still fail or return invalid data. Never expose their
                        // exception, inner exception, cancellation text or plaintext in any diagnostic.
                        throw Unavailable();
                    }

                    await tx.CommitAsync(ct);
                    return result;
                }
                catch
                {
                    // Covers commit/transaction-disposal failure on each retry, before plaintext escapes.
                    result?.Dispose();
                    throw;
                }
            });
        }
        catch (RequestRejectedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            throw Reject(503, "TargetCreationCredentialResolutionUnavailable");
        }
    }

    private static bool ValidField(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4096) return false;
        foreach (var character in value)
            if (char.IsControl(character)) return false;
        // Throwing on an unpaired surrogate is normalized by the protector failure barrier above.
        _ = StrictUtf8.GetByteCount(value);
        return true;
    }

    private static RequestRejectedException Unavailable() => Reject(409, "TargetCreationCredentialUnavailable");
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
