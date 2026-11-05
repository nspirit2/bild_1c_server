using System.Data;
using BuildSystem.Application;
using BuildSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Admin-only SQL one-attempt ledger. Neither this service nor a receipt can execute creation.</summary>
public sealed class SqlTargetCreationOperations(BuildSystemDbContext db, TargetCreationOperationOptions options) : ITargetCreationOperations
{
    private readonly Dictionary<Guid, RestoreIntentCanonicalMapping> mappings = options.CanonicalMappings is { Count: <= 1024 }
        ? new(options.CanonicalMappings) : [];

    public Task<TargetCreationOperationReceipt> RegisterAsync(Actor actor, Guid environmentId, Guid expectedRestoreIntentId, CancellationToken ct)
    {
        if (!options.Enabled) throw Reject(409, "TargetCreationDisabled");
        if (environmentId == Guid.Empty || expectedRestoreIntentId == Guid.Empty) throw Reject(400, "InvalidTargetCreation");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await Lock("BuildSystem:agent-control", ct);
            await Admin(actor, ct);
            var intent = await db.RestoreIntents.AsNoTracking().SingleOrDefaultAsync(i => i.RestoreIntentId == expectedRestoreIntentId && i.EnvironmentId == environmentId, ct)
                ?? throw Reject(409, "TargetCreationMismatch");
            await Admission(intent, ct);
            var prior = await db.TargetCreationOperations.SingleOrDefaultAsync(x => x.RestoreIntentId == intent.RestoreIntentId, ct);
            if (prior is not null)
            {
                var receipt = Receipt(prior, intent, false);
                await tx.CommitAsync(ct);
                return receipt;
            }
            var operation = new CreationOperationRecord { OperationId = Guid.NewGuid(), RestoreIntentId = intent.RestoreIntentId,
                State = TargetCreationOperationState.Registered, Revision = 1, ScheduledJobsMustRemainBlocked = true, Executable = false };
            db.TargetCreationOperations.Add(operation);
            await Audit(actor, "TargetCreationRegistered", operation.OperationId, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Receipt(operation, intent, true);
        });
    }

    public Task<TargetCreationOperationReceipt> BeginAttemptAsync(Actor actor, Guid operationId, long expectedRevision, Guid attemptId, CancellationToken ct)
    {
        if (!options.Enabled) throw Reject(409, "TargetCreationDisabled");
        if (operationId == Guid.Empty || attemptId == Guid.Empty || expectedRevision <= 0) throw Reject(400, "InvalidTargetCreation");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await Lock("BuildSystem:agent-control", ct);
            await Admin(actor, ct);
            var row = await db.TargetCreationOperations.SingleOrDefaultAsync(x => x.OperationId == operationId, ct)
                ?? throw Reject(404, "TargetCreationNotFound");
            var intent = await db.RestoreIntents.AsNoTracking().SingleOrDefaultAsync(x => x.RestoreIntentId == row.RestoreIntentId, ct)
                ?? throw Reject(409, "TargetCreationMismatch");
            await Admission(intent, ct); // Even exact replay requires CURRENT authorization and retained stage binding.
            Validate(row);
            var sameAttempt = row.AttemptId == attemptId && row.AttemptExpectedRevision == expectedRevision;
            if (!sameAttempt && await db.TargetCreationOperations.AnyAsync(x => x.AttemptId == attemptId && x.OperationId != operationId, ct))
                throw Reject(409, "TargetCreationConflict");
            if (!sameAttempt)
            {
                if (row.Revision != expectedRevision) throw Reject(409, "RevisionConflict");
                if (row.State != TargetCreationOperationState.Registered) throw Reject(409, "InvalidTransition");
                row.AttemptId = attemptId;
                row.AttemptExpectedRevision = expectedRevision;
                row.State = TargetCreationOperationState.AttemptRecorded;
                row.Revision++;
                await Audit(actor, "TargetCreationAttemptRecorded", operationId, ct);
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Receipt(row, intent, !sameAttempt);
        });
    }

    public Task<TargetCreationOperationReceipt> QuarantineAsync(Actor actor, Guid operationId, long expectedRevision, TargetCreationQuarantineReason reason, CancellationToken ct)
    {
        if (!options.Enabled) throw Reject(409, "TargetCreationDisabled");
        if (operationId == Guid.Empty || expectedRevision <= 0 || !Enum.IsDefined(reason)) throw Reject(400, "InvalidTargetCreation");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await Lock("BuildSystem:agent-control", ct);
            await Admin(actor, ct);
            var row = await db.TargetCreationOperations.SingleOrDefaultAsync(x => x.OperationId == operationId, ct)
                ?? throw Reject(404, "TargetCreationNotFound");
            // Quarantine must remain possible after a pool, root or agent is disabled. Do not re-admit.
            var intent = await db.RestoreIntents.AsNoTracking().SingleOrDefaultAsync(x => x.RestoreIntentId == row.RestoreIntentId, ct)
                ?? throw Reject(409, "TargetCreationMismatch");
            _ = Receipt(row, intent, false); // Structural pin validation must happen before any mutation or commit.
            if (row.Revision != expectedRevision) throw Reject(409, "RevisionConflict");
            if (row.State == TargetCreationOperationState.ReconciliationRequired) throw Reject(409, "InvalidTransition");
            row.State = TargetCreationOperationState.ReconciliationRequired;
            row.QuarantineReason = reason;
            row.Revision++;
            await Audit(actor, "TargetCreationQuarantined", operationId, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Receipt(row, intent, true);
        });
    }

    private async Task Admin(Actor actor, CancellationToken ct)
    {
        if (actor is null || actor.UserId == Guid.Empty || !actor.IsAdministrator ||
            !await (from role in db.Roles join userRole in db.UserRoles on role.Id equals userRole.RoleId
                join user in db.Users on userRole.UserId equals user.Id
                where user.Id == actor.UserId && role.NormalizedName == "ADMINISTRATOR" select user.Id).AnyAsync(ct))
            throw Reject(403, "TargetCreationForbidden");
    }

    private async Task Admission(RestoreIntentRecord i, CancellationToken ct)
    {
        RequestRejectedException Mismatch() => Reject(409, "TargetCreationMismatch");
        var environment = await db.TestEnvironments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == i.EnvironmentId, ct);
        if (environment is null || environment.RequestId != i.RequestId || environment.ReservationId != i.ReservationId || environment.PoolId != i.PoolId)
            throw Mismatch();
        var rootId = await (from q in db.EnvironmentRequests join v in db.DiskArchiveVersions on q.ArchiveVersionId equals v.Id
            join sourceArchive in db.DiskArchives on v.ArchiveId equals sourceArchive.Id
            where q.Id == environment.RequestId select (Guid?)sourceArchive.RootId).SingleOrDefaultAsync(ct);
        if (rootId is null || rootId != i.RootId) throw Mismatch();
        await Lock("BuildSystem:root:" + rootId.Value.ToString("N"), ct);
        await Lock("BuildSystem:pool:" + environment.PoolId.ToString("N"), ct);
        var request = await db.EnvironmentRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == i.RequestId, ct);
        var job = await db.EnvironmentJobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == i.StageJobId, ct);
        var reservation = await db.EnvironmentReservations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == i.ReservationId, ct);
        var lease = await db.AgentLeases.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == i.StageJobId, ct);
        var artifact = await db.StagedArtifacts.AsNoTracking().SingleOrDefaultAsync(x => x.ArtifactId == i.ArtifactId, ct);
        if (request is null || job is null || reservation is null || lease is null || artifact is null ||
            request.JobId != job.Id || request.EnvironmentId != environment.Id || job.RequestId != request.Id ||
            job.EnvironmentId != environment.Id || request.OwnerId != environment.OwnerId || request.OwnerId != reservation.OwnerId ||
            request.ProjectId != environment.ProjectId || request.ArchiveVersionId != i.ArchiveVersionId ||
            job.Kind != "ProvisionArchive" || !StageAdmission.IsStageJob(job) ||
            artifact.AgentId != i.AgentId || lease.AgentId != i.AgentId ||
            artifact.JobId != job.Id || artifact.ReservationId != reservation.Id ||
            artifact.ArchiveVersionId != i.ArchiveVersionId || artifact.FencingToken != lease.FencingToken ||
            !i.ScheduledJobsMustRemainBlocked || i.Executable || i.FencingToken != lease.FencingToken || i.FencingToken <= 0)
            throw Mismatch();
        var status = await RestoreIntentStatus.ReadAsync(db, environment, request, job, reservation, lease, artifact, "TargetCreationMismatch", ct);
        if (status is null || status.RestoreIntentId != i.RestoreIntentId || status.TargetIncarnation != i.TargetIncarnation) throw Mismatch();
        var root = await db.ArchiveRoots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == i.RootId, ct);
        var pool = await db.EnvironmentPools.AsNoTracking().SingleOrDefaultAsync(x => x.Id == i.PoolId, ct);
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == i.AgentId, ct);
        var archive = await (from version in db.DiskArchiveVersions.AsNoTracking()
            join source in db.DiskArchives.AsNoTracking() on version.ArchiveId equals source.Id
            where version.Id == i.ArchiveVersionId select source).SingleOrDefaultAsync(ct);
        if (root is null || pool is null || agent is null || !root.Enabled || !pool.Enabled || !pool.SupportsDt ||
            !pool.ScheduledJobsSuppressionVerified || !pool.IsolationVerified || !pool.ResourceLimitVerified || agent.State != "Approved" || root.AgentScopeId != agent.Id ||
            pool.AgentScopeId != agent.Id || agent.PoolId != pool.Id || reservation.PoolId != pool.Id ||
            !await db.Projects.AnyAsync(x => x.Id == environment.ProjectId && x.Enabled &&
                x.PreparationProfileVersion == i.PreparationProfileVersion, ct) ||
            !await db.ProjectMemberships.AnyAsync(x => x.UserId == environment.OwnerId && x.ProjectId == environment.ProjectId, ct) ||
            !await db.ArchiveRootProjects.AnyAsync(x => x.RootId == root.Id && x.ProjectId == environment.ProjectId, ct) ||
            !await db.ProjectPools.AnyAsync(x => x.PoolId == pool.Id && x.ProjectId == environment.ProjectId && x.Enabled, ct) ||
            await db.AgentLeases.AnyAsync(x => x.AgentId == agent.Id && (x.State == "Active" || x.State == "NeedsAttention"), ct) ||
            !mappings.TryGetValue(pool.Id, out var map) || map is null ||
            map.CanonicalClusterId != i.CanonicalClusterId || map.CanonicalSqlServerId != i.CanonicalSqlServerId ||
            pool.ClusterId != i.CanonicalClusterId || pool.SqlServerId != i.CanonicalSqlServerId ||
            archive is null || archive.RootId != root.Id || !SqlAgentControl.CanonicalStagePath(archive.RelativePath, "dt"))
            throw Mismatch();
    }

    private static void Validate(CreationOperationRecord row)
    {
        var valid = row.OperationId != Guid.Empty && row.RestoreIntentId != Guid.Empty && !row.Executable && row.ScheduledJobsMustRemainBlocked &&
            (row.State == TargetCreationOperationState.Registered && row.Revision == 1 && row.AttemptId is null && row.AttemptExpectedRevision is null && row.QuarantineReason is null ||
             row.State == TargetCreationOperationState.AttemptRecorded && row.Revision == 2 && row.AttemptId is not null && row.AttemptId != Guid.Empty && row.AttemptExpectedRevision == 1 && row.QuarantineReason is null ||
             row.State == TargetCreationOperationState.ReconciliationRequired && Enum.IsDefined(row.QuarantineReason.GetValueOrDefault()) && row.QuarantineReason is not null &&
             (row.Revision == 2 && row.AttemptId is null && row.AttemptExpectedRevision is null || row.Revision == 3 && row.AttemptId is not null && row.AttemptId != Guid.Empty && row.AttemptExpectedRevision == 1));
        if (!valid) throw Reject(409, "TargetCreationMismatch");
    }

    private static TargetCreationOperationReceipt Receipt(CreationOperationRecord row, RestoreIntentRecord intent, bool newlyRecorded)
    {
        Validate(row);
        if (intent.RestoreIntentId != row.RestoreIntentId || intent.Executable || !intent.ScheduledJobsMustRemainBlocked ||
            intent.RestoreIntentId == Guid.Empty || intent.EnvironmentId == Guid.Empty || intent.StageJobId == Guid.Empty ||
            intent.ReservationId == Guid.Empty || intent.ArtifactId == Guid.Empty || intent.AgentId == Guid.Empty ||
            intent.TargetIncarnation == Guid.Empty || intent.CanonicalClusterId == Guid.Empty || intent.CanonicalSqlServerId == Guid.Empty ||
            intent.InfobaseName != "bs_test_" + intent.ReservationId.ToString("N") ||
            intent.SqlDbName != "bs_test_" + intent.ReservationId.ToString("N"))
            throw Reject(409, "TargetCreationMismatch");
        return new(row.OperationId, intent.RestoreIntentId, intent.EnvironmentId, intent.StageJobId, intent.ReservationId,
            intent.ArtifactId, intent.AgentId, intent.TargetIncarnation, intent.CanonicalClusterId,
            intent.CanonicalSqlServerId, intent.InfobaseName, intent.SqlDbName, row.State, row.Revision,
            row.AttemptId, row.AttemptExpectedRevision, row.QuarantineReason, newlyRecorded);
    }

    private async Task Audit(Actor actor, string action, Guid id, CancellationToken ct)
    {
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
        db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = actor.UserId, Action = action, TargetId = id, CreatedAt = now });
    }
    private Task Lock(string resource, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000; IF @r IN (-1, -3) THROW 51001, 'Transient creation ledger lock contention', 1; IF @r < 0 THROW 51000, 'Creation ledger lock cancelled or invalid', 1;", ct);
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
// end
