using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Bounded read-only operator metadata. Invalid relational bindings are excluded, not repaired.</summary>
public sealed class SqlStageOperations(BuildSystemDbContext db) : IStageOperations
{
    private const string IdCollation = "Latin1_General_100_BIN2";
    private static readonly JsonSerializerOptions CursorJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Position(int Version, long UtcTicks, Guid JobId, string? State, Guid ActorId, bool Administrator);

    public Task<StageOperationsPage> ListAsync(Actor actor, StageOperationsQuery query, CancellationToken ct)
    {
        if (actor is null || actor.UserId == Guid.Empty) throw new RequestRejectedException(403, "Forbidden");
        if (query is null || query.Limit is < 1 or > 100 ||
            query.State is not (null or "Pending" or "Leased" or "StageCompleted" or "NeedsAttention")) throw InvalidQuery();
        var position = Decode(query.Cursor, actor, query.State);
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var rows = from job in db.EnvironmentJobs.AsNoTracking()
                       join request in db.EnvironmentRequests.AsNoTracking() on job.RequestId equals request.Id
                       join environment in db.TestEnvironments.AsNoTracking() on job.EnvironmentId equals environment.Id
                       join reservation in db.EnvironmentReservations.AsNoTracking() on environment.ReservationId equals reservation.Id
                       where job.Kind == "ProvisionArchive" && request.JobId == job.Id &&
                           request.EnvironmentId == environment.Id && environment.RequestId == request.Id &&
                           request.OwnerId == environment.OwnerId && request.OwnerId == reservation.OwnerId &&
                           request.ProjectId == environment.ProjectId && reservation.PoolId == environment.PoolId &&
                           reservation.StagingBytes >= 0 && (query.State == null || job.State == query.State) &&
                           (actor.IsAdministrator || (environment.OwnerId == actor.UserId &&
                               db.ProjectMemberships.Any(m => m.UserId == actor.UserId && m.ProjectId == environment.ProjectId) &&
                               db.Projects.Any(p => p.Id == environment.ProjectId && p.Enabled)))
                       select new { job, request, environment, reservation };
            if (position is not null)
            {
                var createdAt = new DateTimeOffset(position.UtcTicks, TimeSpan.Zero);
                var id = position.JobId.ToString("D");
                rows = rows.Where(x => x.job.CreatedAt < createdAt || (x.job.CreatedAt == createdAt &&
                    string.Compare(EF.Functions.Collate(x.job.Id.ToString().ToLower(), IdCollation), id) < 0));
            }
            // BIN2 string ordering is deliberate: SQL uniqueidentifier and .NET Guid ordering differ.
            // Exactly one bounded data query; no per-row lookups, OFFSET, total COUNT or entity materialization.
            var items = await rows.OrderByDescending(x => x.job.CreatedAt)
                .ThenByDescending(x => EF.Functions.Collate(x.job.Id.ToString().ToLower(), IdCollation))
                .Select(x => new StageOperationView(x.environment.Id, x.request.Id, x.job.Id, x.job.State,
                    x.environment.State, x.reservation.State, x.reservation.StagingBytes,
                    x.environment.HasUnsettledProvisioningOperation, x.job.CreatedAt))
                .Take(query.Limit + 1).ToListAsync(ct);
            var hasMore = items.Count > query.Limit;
            if (hasMore) items.RemoveAt(query.Limit);
            var next = hasMore ? Encode(new Position(1, items[^1].CreatedAt.UtcTicks, items[^1].JobId,
                query.State, actor.UserId, actor.IsAdministrator)) : null;
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            await tx.CommitAsync(ct);
            return new StageOperationsPage(items, next, now);
        });
    }

    private static string Encode(Position position) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(position, CursorJson))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Position? Decode(string? cursor, Actor actor, string? state)
    {
        if (cursor is null) return null;
        if (cursor.Length is < 1 or > 512 || cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw InvalidQuery();
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var position = JsonSerializer.Deserialize<Position>(Convert.FromBase64String(encoded), CursorJson);
            if (position is null || position.Version != 1 || position.UtcTicks < DateTimeOffset.MinValue.UtcTicks ||
                position.UtcTicks > DateTimeOffset.MaxValue.UtcTicks || position.JobId == Guid.Empty ||
                position.State != state || position.ActorId != actor.UserId || position.Administrator != actor.IsAdministrator ||
                Encode(position) != cursor) throw InvalidQuery();
            return position;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or OverflowException)
        { throw InvalidQuery(); }
    }

    private static RequestRejectedException InvalidQuery() => new(400, "InvalidStageListQuery");
}
// EOF
