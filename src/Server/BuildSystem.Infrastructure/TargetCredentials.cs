using System.Data;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>
/// Internal administrator-only storage; not an execution grant or a credential resolver.
/// A stored binding has no active pointer. Every future consumer must pin an explicit credential ID.
/// </summary>
public sealed class SqlTargetCredentials(BuildSystemDbContext db, TargetCredentialOptions options,
    ITargetCredentialProtector protector) : ITargetCredentials
{
    public Task<TargetCredentialMetadata> StoreAsync(Actor actor, TargetCredentialBinding binding,
        TargetCredentialInput input, CancellationToken ct)
    {
        Enabled();
        if (binding is null || binding.CredentialId == Guid.Empty || binding.CanonicalClusterId == Guid.Empty ||
            binding.CanonicalSqlServerId == Guid.Empty || binding.EndpointMappingId == Guid.Empty || input is null)
            throw Reject(400, "InvalidTargetCredential");
        return Transaction(async () =>
        {
            await Admin(actor, ct);
            // IDs are append-only, not replay or overwrite authority. Never compare/decrypt a prior secret.
            if (await db.TargetCredentials.AnyAsync(x => x.CredentialId == binding.CredentialId, ct))
                throw Reject(409, "CredentialConflict");
            string ciphertext;
            try
            {
                ciphertext = protector.Protect(new(binding.CredentialId, binding.CanonicalClusterId,
                    binding.CanonicalSqlServerId, binding.EndpointMappingId),
                    new(input.ClusterUser, input.ClusterPassword, input.SqlUser, input.SqlPassword));
                if (string.IsNullOrWhiteSpace(ciphertext) || ciphertext.Length > TargetCredentialStorage.MaxCiphertextCharacters)
                    throw new TargetCredentialProtectionException();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw Reject(503, "TargetCredentialsUnavailable"); }
            var now = await Now(ct);
            var row = new TargetCredentialRecord
            {
                CredentialId = binding.CredentialId, CanonicalClusterId = binding.CanonicalClusterId,
                CanonicalSqlServerId = binding.CanonicalSqlServerId, EndpointMappingId = binding.EndpointMappingId,
                Ciphertext = ciphertext, State = TargetCredentialState.Stored, Revision = 1, CreatedAt = now
            };
            db.TargetCredentials.Add(row);
            Audit(actor, "TargetCredentialStored", row.CredentialId, now);
            await db.SaveChangesAsync(ct);
            return Metadata(row);
        }, ct);
    }

    public Task<TargetCredentialMetadata> RevokeAsync(Actor actor, Guid credentialId, long expectedRevision, CancellationToken ct)
    {
        Enabled();
        if (credentialId == Guid.Empty || expectedRevision <= 0) throw Reject(400, "InvalidTargetCredential");
        return Transaction(async () =>
        {
            await Admin(actor, ct);
            var row = await Credential(credentialId, ct);
            _ = Metadata(row);
            if (row.Revision != expectedRevision) throw Reject(409, "RevisionConflict");
            if (row.State != TargetCredentialState.Stored) throw Reject(409, "InvalidTransition");
            var now = await Now(ct);
            if (now < row.CreatedAt) throw Reject(409, "InvalidServerTime");
            row.State = TargetCredentialState.Revoked;
            row.Revision = 2;
            row.RevokedAt = now;
            // Retain the exact encrypted envelope and immutable purpose binding, including after revocation.
            Audit(actor, "TargetCredentialRevoked", row.CredentialId, now);
            await db.SaveChangesAsync(ct);
            return Metadata(row);
        }, ct);
    }

    public Task<TargetCredentialMetadata> GetAsync(Actor actor, Guid credentialId, CancellationToken ct)
    {
        Enabled();
        if (credentialId == Guid.Empty) throw Reject(400, "InvalidTargetCredential");
        return Transaction(async () =>
        {
            await Admin(actor, ct); // Current DB authorization precedes every credential lookup, even metadata reads.
            return Metadata(await Credential(credentialId, ct));
        }, ct);
    }

    private void Enabled()
    {
        if (!options.Enabled) throw Reject(503, "TargetCredentialsUnavailable");
    }

    private Task<TargetCredentialMetadata> Transaction(Func<Task<TargetCredentialMetadata>> action, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={"BuildSystem:agent-control"}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient target credential lock contention', 1; IF @r < 0 THROW 51000, 'Target credential lock cancelled or invalid', 1;", ct);
            var result = await action();
            await tx.CommitAsync(ct);
            return result;
        });

    private async Task Admin(Actor actor, CancellationToken ct)
    {
        if (actor is null || actor.UserId == Guid.Empty ||
            !await (from role in db.Roles join userRole in db.UserRoles on role.Id equals userRole.RoleId
                join user in db.Users on userRole.UserId equals user.Id
                where user.Id == actor.UserId && role.NormalizedName == "ADMINISTRATOR" select user.Id).AnyAsync(ct))
            throw Reject(403, "TargetCreationForbidden");
    }

    private async Task<TargetCredentialRecord> Credential(Guid id, CancellationToken ct) =>
        await db.TargetCredentials.SingleOrDefaultAsync(x => x.CredentialId == id, ct)
        ?? throw Reject(404, "TargetCredentialNotFound");

    private Task<DateTimeOffset> Now(CancellationToken ct) => db.Database.SqlQuery<DateTimeOffset>(
        $"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);

    private void Audit(Actor actor, string action, Guid id, DateTimeOffset now) =>
        db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = actor.UserId, Action = action,
            TargetId = id, CreatedAt = now });

    private static TargetCredentialMetadata Metadata(TargetCredentialRecord row)
    {
        var valid = row.CredentialId != Guid.Empty && row.CanonicalClusterId != Guid.Empty &&
            row.CanonicalSqlServerId != Guid.Empty && row.EndpointMappingId != Guid.Empty &&
            !string.IsNullOrWhiteSpace(row.Ciphertext) && row.Ciphertext.Length <= TargetCredentialStorage.MaxCiphertextCharacters &&
            row.CreatedAt.Offset == TimeSpan.Zero &&
            (row.State == TargetCredentialState.Stored && row.Revision == 1 && row.RevokedAt is null ||
             row.State == TargetCredentialState.Revoked && row.Revision == 2 && row.RevokedAt is { } revoked &&
             revoked.Offset == TimeSpan.Zero && revoked >= row.CreatedAt);
        if (!valid) throw Reject(409, "TargetCredentialMismatch");
        return new(new(row.CredentialId, row.CanonicalClusterId, row.CanonicalSqlServerId, row.EndpointMappingId),
            row.State, row.Revision, row.CreatedAt, row.RevokedAt);
    }

    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
