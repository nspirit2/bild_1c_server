using System.Data;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Historical order metadata only; never loads execution snapshots or authority material.</summary>
public sealed class SqlEnvironmentOrderSummary(BuildSystemDbContext db) : IEnvironmentOrderSummary
{
    public Task<EnvironmentOrderSummaryView?> GetAsync(Actor actor, Guid environmentId, CancellationToken ct)
    {
        if (actor is null || actor.UserId == Guid.Empty) throw new RequestRejectedException(403, "Forbidden");
        if (environmentId == Guid.Empty) throw new RequestRejectedException(400, "InvalidEnvironmentOrderSummary");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // First lookup is the visibility boundary. Never probe request, job or source for an absent environment.
            var environment = await db.TestEnvironments.AsNoTracking()
                .Where(e => e.Id == environmentId && (actor.IsAdministrator ||
                    (e.OwnerId == actor.UserId && db.ProjectMemberships.Any(m =>
                        m.UserId == actor.UserId && m.ProjectId == e.ProjectId))))
                .Select(e => new
                {
                    e.Id, e.ProjectId, e.OwnerId, e.RequestId,
                    DisplayName = EF.Functions.DataLength(e.DisplayName) > 240 ? "" : e.DisplayName,
                    State = EF.Functions.DataLength(e.State) > 128 ? "" : e.State
                }).SingleOrDefaultAsync(ct);
            if (environment is null)
            {
                await tx.CommitAsync(ct);
                return null;
            }
            var request = await db.EnvironmentRequests.AsNoTracking().Where(r => r.Id == environment.RequestId)
                .Select(r => new
                {
                    r.Id, r.EnvironmentId, r.OwnerId, r.ProjectId, r.JobId, r.ArchiveVersionId,
                    State = EF.Functions.DataLength(r.State) > 128 ? "" : r.State,
                    r.CreatedAt, r.TtlDays
                }).SingleOrDefaultAsync(ct);
            if (environment.Id == Guid.Empty || environment.ProjectId == Guid.Empty || environment.OwnerId == Guid.Empty ||
                environment.RequestId == Guid.Empty || request is null || request.Id == Guid.Empty || request.EnvironmentId != environment.Id ||
                request.OwnerId != environment.OwnerId || request.ProjectId != environment.ProjectId ||
                request.JobId == Guid.Empty || request.ArchiveVersionId == Guid.Empty)
                throw Inconsistent();
            var job = await db.EnvironmentJobs.AsNoTracking().Where(j => j.Id == request.JobId)
                .Select(j => new
                {
                    j.Id, j.RequestId, j.EnvironmentId,
                    Kind = EF.Functions.DataLength(j.Kind) > 128 ? "" : j.Kind,
                    State = EF.Functions.DataLength(j.State) > 128 ? "" : j.State
                }).SingleOrDefaultAsync(ct);
            if (job is null || job.Id == Guid.Empty || job.RequestId != request.Id || job.EnvironmentId != environment.Id ||
                job.Kind is not ("ProvisionArchive" or "ObserveArchiveStage")) throw Inconsistent();
            // An archived version is historical identity, not renewed source permission/readiness.
            var source = await db.DiskArchiveVersions.AsNoTracking().Where(v => v.Id == request.ArchiveVersionId)
                .Select(v => new { v.Id,
                    Format = EF.Functions.DataLength(v.Format) > 6 ? "" : v.Format,
                    Sha256 = EF.Functions.DataLength(v.Sha256) > 128 ? "" : v.Sha256,
                    v.SizeBytes }).SingleOrDefaultAsync(ct);
            if (source is null || source.Id == Guid.Empty || source.Format is not ("dt" or "bak") ||
                source.SizeBytes <= 0 || source.Sha256 is null || source.Sha256.Length != 64 ||
                !source.Sha256.All(IsAsciiHex) || !ValidName(environment.DisplayName) ||
                !ValidState(environment.State) || !ValidState(request.State) || !ValidState(job.State) ||
                request.TtlDays is < 1 or > 36500 || request.CreatedAt <= DateTimeOffset.UnixEpoch ||
                request.CreatedAt.Offset != TimeSpan.Zero) throw Inconsistent();
            var result = new EnvironmentOrderSummaryView(environment.Id, environment.ProjectId,
                environment.DisplayName, environment.State, request.Id, request.State, request.CreatedAt,
                request.TtlDays, job.Id, job.Kind, job.State,
                new EnvironmentOrderSourceView(source.Id, source.Format, source.Sha256, source.SizeBytes));
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private static bool ValidName(string? name) => name is { Length: >= 1 and <= 120 } &&
        !string.IsNullOrWhiteSpace(name) && name == name.Trim() && !name.Any(char.IsControl);
    private static bool ValidState(string? state) => state is { Length: >= 1 and <= 64 } &&
        char.IsAsciiLetter(state[0]) && state.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    private static bool IsAsciiHex(char c) => c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
    private static RequestRejectedException Inconsistent() => new(409, "EnvironmentOrderSummaryInconsistent");
}
// EOF
