using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Read-only selection catalog. Order creation rechecks all source, pool and quota authority.</summary>
public sealed class SqlArchiveCatalogBrowse(BuildSystemDbContext db) : IArchiveCatalogBrowse
{
    private const string IdCollation = "Latin1_General_100_BIN2";
    private static readonly JsonSerializerOptions CursorJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Position(int Version, Guid ArchiveVersionId, Guid ProjectId, Guid ActorId, bool Administrator);

    public Task<ArchiveCatalogPage> ListAsync(Actor actor, Guid projectId, ArchiveCatalogQuery query,
        CancellationToken ct = default)
    {
        if (actor is null || actor.UserId == Guid.Empty) throw new RequestRejectedException(403, "Forbidden");
        if (query is null || query.Limit is < 1 or > 100) throw InvalidQuery();
        var position = Decode(query.Cursor, actor, projectId);
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // Match order creation: administrators bypass membership, never project.Enabled or source ACL.
            if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId && p.Enabled &&
                (actor.IsAdministrator || db.ProjectMemberships.Any(m => m.ProjectId == p.Id && m.UserId == actor.UserId)), ct))
                throw new RequestRejectedException(404, "ProjectNotFound");
            var rows = from version in db.DiskArchiveVersions.AsNoTracking()
                       join archive in db.DiskArchives.AsNoTracking() on version.ArchiveId equals archive.Id
                       join root in db.ArchiveRoots.AsNoTracking() on archive.RootId equals root.Id
                       where root.Enabled && version.State == "Ready" && version.SizeBytes > 0 &&
                           version.EstimatedDbBytes > 0 && (version.Format == "dt" || version.Format == "bak") &&
                           db.ArchiveRootProjects.Any(acl => acl.RootId == root.Id && acl.ProjectId == projectId)
                       select version;
            if (position is not null)
            {
                var id = position.ArchiveVersionId.ToString("D");
                rows = rows.Where(v => string.Compare(EF.Functions.Collate(v.Id.ToString().ToLower(), IdCollation), id) > 0);
            }
            // Consistent SQL string ordering avoids the SQL/.NET uniqueidentifier ordering mismatch.
            // Bounded output, not bounded SQL work: this normalized key can require a scan/sort.
            var items = await rows.OrderBy(v => EF.Functions.Collate(v.Id.ToString().ToLower(), IdCollation))
                .Select(v => new ArchiveCatalogView(v.Id, v.Format, v.SizeBytes, v.EstimatedDbBytes))
                .Take(query.Limit + 1).ToListAsync(ct);
            var hasMore = items.Count > query.Limit;
            if (hasMore) items.RemoveAt(query.Limit);
            var next = hasMore ? Encode(new Position(1, items[^1].ArchiveVersionId, projectId, actor.UserId,
                actor.IsAdministrator)) : null;
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            await tx.CommitAsync(ct);
            return new ArchiveCatalogPage(items, next, now);
        });
    }

    private static string Encode(Position position) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(position, CursorJson))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Position? Decode(string? cursor, Actor actor, Guid projectId)
    {
        if (cursor is null) return null;
        if (cursor.Length is < 1 or > 512 || cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw InvalidQuery();
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var position = JsonSerializer.Deserialize<Position>(Convert.FromBase64String(encoded), CursorJson);
            if (position is null || position.Version != 1 || position.ArchiveVersionId == Guid.Empty ||
                position.ProjectId != projectId || position.ActorId != actor.UserId ||
                position.Administrator != actor.IsAdministrator || Encode(position) != cursor) throw InvalidQuery();
            return position;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or OverflowException)
        { throw InvalidQuery(); }
    }

    private static RequestRejectedException InvalidQuery() => new(400, "InvalidArchiveCatalogQuery");
}
// EOF
