using System.Data;
using BuildSystem.Application;
using BuildSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Administrator and assigned-agent SQL metadata barriers; never external execution authorization.</summary>
public sealed class SqlTargetCreationPermits(BuildSystemDbContext db, TargetCreationPermitOptions permitOptions,
    TargetCreationOperationOptions creationOptions) : ITargetCreationPermits, ITargetCreationPermitAgentStarts
{
    private readonly SqlTargetCreationOperations ledger = new(db, creationOptions);

    public Task<TargetCreationPermitReceipt> IssueAsync(Actor actor, Guid operationId, long expectedOperationRevision,
        Guid permitId, Guid attemptId, CancellationToken ct)
        => IssueCore(actor, operationId, expectedOperationRevision, permitId, attemptId, null, null, ct);

    public Task<TargetCreationPermitReceipt> IssueWithCredentialAsync(Actor actor, Guid operationId,
        long expectedOperationRevision, Guid permitId, Guid attemptId, Guid credentialId, Guid endpointMappingId,
        CancellationToken ct)
    {
        Enabled();
        CredentialBindingEnabled();
        if (credentialId == Guid.Empty || endpointMappingId == Guid.Empty)
            throw Reject(400, "InvalidTargetCreationPermit");
        return IssueCore(actor, operationId, expectedOperationRevision, permitId, attemptId, credentialId, endpointMappingId, ct);
    }

    private Task<TargetCreationPermitReceipt> IssueCore(Actor actor, Guid operationId, long expectedOperationRevision,
        Guid permitId, Guid attemptId, Guid? credentialId, Guid? endpointMappingId, CancellationToken ct)
    {
        Enabled();
        if (operationId == Guid.Empty || permitId == Guid.Empty || attemptId == Guid.Empty || expectedOperationRevision <= 0 ||
            !BuildSystemDbContext.ValidCredentialPins(credentialId, endpointMappingId))
            throw Reject(400, "InvalidTargetCreationPermit");
        return Transaction(async () =>
        {
            await ledger.Admin(actor, ct);
            var operation = await Operation(operationId, ct);
            var intent = await Intent(operation, ct);
            await ledger.Admission(intent, ct); // Current admission precedes even an exact replay lookup.
            _ = SqlTargetCreationOperations.Receipt(operation, intent, false);
            if (credentialId is { } credential)
                await RequireCurrentCredential(credential, endpointMappingId!.Value, intent, await Now(ct), ct);
            if (operation.State == TargetCreationOperationState.ReconciliationRequired)
                throw Reject(409, "InvalidTransition");
            var prior = await db.TargetCreationPermits.SingleOrDefaultAsync(x => x.OperationId == operationId, ct);
            if (prior is not null)
            {
                if (prior.OperationId != operationId || prior.PermitId != permitId || prior.AttemptId != attemptId ||
                    prior.ExpectedOperationRevision != expectedOperationRevision ||
                    prior.CredentialId != credentialId || prior.EndpointMappingId != endpointMappingId)
                    throw Reject(409, "PermitConflict");
                return Receipt(prior, operation, intent, false); // No deadline renewal, even after revocation/expiry.
            }
            if (await db.TargetCreationPermits.AnyAsync(x => x.PermitId == permitId || x.AttemptId == attemptId, ct))
                throw Reject(409, "PermitConflict");
            if (operation.Revision != expectedOperationRevision) throw Reject(409, "RevisionConflict");
            if (operation.State != TargetCreationOperationState.Registered || operation.Revision != 1)
                throw Reject(409, "InvalidTransition");
            if (await db.TargetCreationOperations.AnyAsync(x => x.AttemptId == attemptId, ct))
                throw Reject(409, "PermitConflict");
            var now = await Now(ct);
            var row = new CreationPermitRecord { PermitId = permitId, OperationId = operationId, AttemptId = attemptId,
                CredentialId = credentialId, EndpointMappingId = endpointMappingId,
                ExpectedOperationRevision = expectedOperationRevision, IssuedAt = now, StartBefore = now.AddMinutes(5),
                State = TargetCreationPermitState.Prepared, Revision = 1, Executable = false, ScheduledJobsMustRemainBlocked = true };
            var result = Receipt(row, operation, intent, true);
            db.TargetCreationPermits.Add(row);
            await ledger.Audit(actor, "TargetCreationPermitIssued", permitId, ct);
            await db.SaveChangesAsync(ct);
            return result;
        }, ct);
    }

    public Task<TargetCreationPermitReceipt> RecordStartAsync(Actor actor, Guid permitId, long expectedPermitRevision,
        Guid attemptId, CancellationToken ct)
    {
        Enabled();
        if (permitId == Guid.Empty || attemptId == Guid.Empty || expectedPermitRevision <= 0)
            throw Reject(400, "InvalidTargetCreationPermit");
        return Transaction(async () =>
        {
            await ledger.Admin(actor, ct); // ADMIN barrier only, not authentication of the pinned agent.
            var row = await Permit(permitId, ct);
            var operation = await Operation(row.OperationId, ct);
            var intent = await Intent(operation, ct);
            await ledger.Admission(intent, ct);
            return await RecordStartCore(actor, row, operation, intent, expectedPermitRevision, attemptId, await Now(ct), ct);
        }, ct);
    }

    /// <summary>Records nonexecuting start metadata for the currently authenticated, assigned agent only.</summary>
    public Task<TargetCreationPermitReceipt> RecordAgentStartAsync(string token, Guid permitId,
        long expectedPermitRevision, Guid attemptId, CancellationToken ct)
    {
        Enabled();
        if (!permitOptions.AgentStartEnabled) throw Reject(409, "TargetCreationPermitDisabled");
        if (permitId == Guid.Empty || attemptId == Guid.Empty || expectedPermitRevision <= 0)
            throw Reject(400, "InvalidTargetCreationPermit");
        return Transaction(async () =>
        {
            // Recheck current credentials on every strategy retry under the global agent-control lock.
            // Even malformed credentials are rejected before any permit-existence lookup.
            if (string.IsNullOrEmpty(token) || token.Length > 256) throw Reject(401, "AgentUnauthorized");
            var hash = TokenProtector.Hash(token);
            var agent = await db.Agents.SingleOrDefaultAsync(x => x.TokenHash == hash && x.State == "Approved", ct);
            if (agent is null || !TokenProtector.Verify(token, agent.TokenHash!)) throw Reject(401, "AgentUnauthorized");
            var row = await Permit(permitId, ct);
            var operation = await Operation(row.OperationId, ct);
            var intent = await Intent(operation, ct);
            if (agent.Id != intent.AgentId) throw Reject(403, "TargetCreationPermitForbidden");
            await ledger.Admission(intent, ct); // Current admission is required even for an exact replay.
            return await RecordStartCore(new Actor(agent.Id, false), row, operation, intent,
                expectedPermitRevision, attemptId, await Now(ct), ct);
        }, ct);
    }

    // Shared metadata transition only: callers own authentication, admission and the single transaction.
    private async Task<TargetCreationPermitReceipt> RecordStartCore(Actor actor, CreationPermitRecord row,
        CreationOperationRecord operation, RestoreIntentRecord intent, long expectedPermitRevision,
        Guid attemptId, DateTimeOffset now, CancellationToken ct)
    {
        _ = Receipt(row, operation, intent, false);
        if (row.CredentialId is { } credentialId)
        {
            CredentialBindingEnabled();
            await RequireCurrentCredential(credentialId, row.EndpointMappingId!.Value, intent, now, ct);
        }
        if (operation.State == TargetCreationOperationState.ReconciliationRequired)
            throw Reject(409, "InvalidTransition");
        if (row.AttemptId != attemptId) throw Reject(409, "PermitConflict");
        if (now < row.IssuedAt) throw Reject(409, "InvalidServerTime");
        if (row.StartedAt is not null && row.StartExpectedRevision == expectedPermitRevision)
            return Receipt(row, operation, intent, false); // Historical receipt, never a new authorization.
        if (row.Revision != expectedPermitRevision) throw Reject(409, "RevisionConflict");
        if (row.State != TargetCreationPermitState.Prepared) throw Reject(409, "InvalidTransition");
        if (now >= row.StartBefore) throw Reject(409, "PermitExpired");
        if (operation.Revision != row.ExpectedOperationRevision) throw Reject(409, "RevisionConflict");
        if (operation.State != TargetCreationOperationState.Registered || operation.Revision != 1)
            throw Reject(409, "InvalidTransition");
        if (await db.TargetCreationOperations.AnyAsync(x => x.AttemptId == attemptId && x.OperationId != operation.OperationId, ct))
            throw Reject(409, "PermitConflict");
        operation.AttemptId = attemptId;
        operation.AttemptExpectedRevision = row.ExpectedOperationRevision;
        operation.State = TargetCreationOperationState.AttemptRecorded;
        operation.Revision = 2;
        row.State = TargetCreationPermitState.StartRecorded;
        row.Revision = 2;
        row.StartedAt = now;
        row.StartExpectedRevision = expectedPermitRevision;
        var result = Receipt(row, operation, intent, true);
        await ledger.Audit(actor, "TargetCreationAttemptRecorded", operation.OperationId, ct);
        await ledger.Audit(actor, "TargetCreationPermitStartRecorded", row.PermitId, ct);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public Task<TargetCreationPermitReceipt> RevokeAsync(Actor actor, Guid permitId, long expectedPermitRevision, CancellationToken ct)
    {
        Enabled();
        if (permitId == Guid.Empty || expectedPermitRevision <= 0) throw Reject(400, "InvalidTargetCreationPermit");
        return Transaction(async () =>
        {
            await ledger.Admin(actor, ct);
            var row = await Permit(permitId, ct);
            var operation = await Operation(row.OperationId, ct);
            var intent = await Intent(operation, ct);
            _ = Receipt(row, operation, intent, false); // Revocation stays possible after admission is withdrawn.
            if (row.Revision != expectedPermitRevision) throw Reject(409, "RevisionConflict");
            if (row.State is not (TargetCreationPermitState.Prepared or TargetCreationPermitState.StartRecorded))
                throw Reject(409, "InvalidTransition");
            if (await Now(ct) < row.IssuedAt) throw Reject(409, "InvalidServerTime");
            row.State = TargetCreationPermitState.Revoked;
            row.Revision++;
            var result = Receipt(row, operation, intent, true);
            await ledger.Audit(actor, "TargetCreationPermitRevoked", permitId, ct);
            await db.SaveChangesAsync(ct);
            return result;
        }, ct);
    }

    public Task<TargetCreationPermitReceipt> GetAsync(Actor actor, Guid permitId, CancellationToken ct)
    {
        Enabled();
        if (permitId == Guid.Empty) throw Reject(400, "InvalidTargetCreationPermit");
        return Transaction(async () =>
        {
            await ledger.Admin(actor, ct);
            var row = await Permit(permitId, ct);
            var operation = await Operation(row.OperationId, ct);
            return Receipt(row, operation, await Intent(operation, ct), false);
        }, ct);
    }

    private void Enabled()
    {
        if (!permitOptions.Enabled || !creationOptions.Enabled) throw Reject(409, "TargetCreationPermitDisabled");
    }

    private void CredentialBindingEnabled()
    {
        if (!permitOptions.CredentialBindingEnabled) throw Reject(409, "TargetCreationPermitDisabled");
    }

    // Metadata projection only: never select, decrypt or transport a protected credential envelope.
    // Storage CHECKs and its immutable write guard validate ciphertext; no secret is needed for this barrier.
    private async Task RequireCurrentCredential(Guid credentialId, Guid endpointMappingId, RestoreIntentRecord intent,
        DateTimeOffset now, CancellationToken ct)
    {
        var credential = await db.TargetCredentials.AsNoTracking().Where(x => x.CredentialId == credentialId)
            .Select(x => new { x.CredentialId, x.CanonicalClusterId, x.CanonicalSqlServerId, x.EndpointMappingId,
                x.State, x.Revision, x.CreatedAt, x.RevokedAt }).SingleOrDefaultAsync(ct);
        if (credential is null || credential.CredentialId == Guid.Empty ||
            credential.CanonicalClusterId == Guid.Empty || credential.CanonicalSqlServerId == Guid.Empty ||
            credential.EndpointMappingId == Guid.Empty || credential.EndpointMappingId != endpointMappingId ||
            credential.CanonicalClusterId != intent.CanonicalClusterId ||
            credential.CanonicalSqlServerId != intent.CanonicalSqlServerId ||
            credential.State != TargetCredentialState.Stored || credential.Revision != 1 || credential.RevokedAt is not null ||
            credential.CreatedAt.Offset != TimeSpan.Zero || now.Offset != TimeSpan.Zero || credential.CreatedAt > now)
            throw Reject(409, "TargetCreationCredentialUnavailable");
    }

    private Task<TargetCreationPermitReceipt> Transaction(Func<Task<TargetCreationPermitReceipt>> action, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await ledger.Lock("BuildSystem:agent-control", ct);
            var result = await action();
            await tx.CommitAsync(ct);
            return result;
        });

    private async Task<CreationPermitRecord> Permit(Guid id, CancellationToken ct) =>
        await db.TargetCreationPermits.SingleOrDefaultAsync(x => x.PermitId == id, ct)
        ?? throw Reject(404, "TargetCreationPermitNotFound");
    private async Task<CreationOperationRecord> Operation(Guid id, CancellationToken ct) =>
        await db.TargetCreationOperations.SingleOrDefaultAsync(x => x.OperationId == id, ct)
        ?? throw Reject(404, "TargetCreationNotFound");
    private async Task<RestoreIntentRecord> Intent(CreationOperationRecord operation, CancellationToken ct) =>
        await db.RestoreIntents.AsNoTracking().SingleOrDefaultAsync(x => x.RestoreIntentId == operation.RestoreIntentId, ct)
        ?? throw Reject(409, "TargetCreationMismatch");
    private Task<DateTimeOffset> Now(CancellationToken ct) => db.Database.SqlQuery<DateTimeOffset>(
        $"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);

    private static TargetCreationPermitReceipt Receipt(CreationPermitRecord row, CreationOperationRecord operation,
        RestoreIntentRecord intent, bool newlyRecorded)
    {
        var operationReceipt = SqlTargetCreationOperations.Receipt(operation, intent, false);
        var valid = row.PermitId != Guid.Empty && row.OperationId == operation.OperationId && row.AttemptId != Guid.Empty &&
            row.ExpectedOperationRevision == 1 && !row.Executable && row.ScheduledJobsMustRemainBlocked &&
            BuildSystemDbContext.ValidCredentialPins(row.CredentialId, row.EndpointMappingId) &&
            row.IssuedAt.Offset == TimeSpan.Zero && row.StartBefore.Offset == TimeSpan.Zero &&
            row.IssuedAt <= DateTimeOffset.MaxValue.AddMinutes(-5) && row.StartBefore == row.IssuedAt.AddMinutes(5) &&
            (row.StartedAt is null || row.StartedAt.Value.Offset == TimeSpan.Zero && row.StartedAt >= row.IssuedAt && row.StartedAt < row.StartBefore) &&
            (row.State == TargetCreationPermitState.Prepared && row.Revision == 1 && row.StartedAt is null && row.StartExpectedRevision is null ||
             row.State == TargetCreationPermitState.StartRecorded && row.Revision == 2 && row.StartedAt is not null && row.StartExpectedRevision == 1 ||
             row.State == TargetCreationPermitState.Revoked &&
             (row.Revision == 2 && row.StartedAt is null && row.StartExpectedRevision is null ||
              row.Revision == 3 && row.StartedAt is not null && row.StartExpectedRevision == 1));
        if (!valid || operation.AttemptId is not null && (operation.AttemptId != row.AttemptId || operation.AttemptExpectedRevision != row.ExpectedOperationRevision) ||
            row.StartedAt is not null && operation.AttemptId is null)
            throw Reject(409, "TargetCreationPermitMismatch");
        return new(row.PermitId, row.OperationId, row.AttemptId, row.ExpectedOperationRevision, row.IssuedAt,
            row.StartBefore, row.State, row.Revision, row.StartedAt, row.StartExpectedRevision, newlyRecorded, operationReceipt)
            { CredentialId = row.CredentialId, EndpointMappingId = row.EndpointMappingId };
    }

    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
