using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Read-only, bounded human project picker. Order creation rechecks current authority.</summary>
public sealed class SqlProjectBrowse(BuildSystemDbContext db) : IProjectBrowse
{
    private const string IdCollation = "Latin1_General_100_BIN2";
    private static readonly JsonSerializerOptions CursorJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Position(int Version, Guid ProjectId, Guid ActorId, bool Administrator);

    public Task<ProjectBrowsePage> ListAsync(Actor actor, ProjectBrowseQuery query, CancellationToken ct = default)
    {
        if (actor is null || actor.UserId == Guid.Empty) throw new RequestRejectedException(403, "Forbidden");
        if (query is null || query.Limit is < 1 or > 100) throw InvalidQuery();
        var position = Decode(query.Cursor, actor);
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // Match order creation: enabled projects, with current membership except for administrators.
            var rows = db.Projects.AsNoTracking().Where(project => project.Enabled &&
                (actor.IsAdministrator || db.ProjectMemberships.Any(m => m.ProjectId == project.Id && m.UserId == actor.UserId)));
            if (position is not null)
            {
                var id = position.ProjectId.ToString("D");
                rows = rows.Where(project => string.Compare(EF.Functions.Collate(project.Id.ToString().ToLower(), IdCollation), id) > 0);
            }
            // SQL projection bounds hostile or legacy Name values before they reach application memory.
            // BIN2 string key matches the cursor comparator; SQL uniqueidentifier ordering differs from .NET Guid ordering.
            var items = await rows.OrderBy(project => EF.Functions.Collate(project.Id.ToString().ToLower(), IdCollation))
                .Select(project => new ProjectBrowseView(project.Id,
                    EF.Functions.DataLength(project.Name) > 512
                        ? EF.Functions.Collate(project.Name, IdCollation).Substring(0, 256) : project.Name))
                .Take(query.Limit + 1).ToListAsync(ct);
            var hasMore = items.Count > query.Limit;
            if (hasMore) items.RemoveAt(query.Limit);
            var next = hasMore ? Encode(new Position(1, items[^1].ProjectId, actor.UserId, actor.IsAdministrator)) : null;
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            await tx.CommitAsync(ct);
            return new ProjectBrowsePage(items, next, now);
        });
    }

    private static string Encode(Position position) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(position, CursorJson))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Position? Decode(string? cursor, Actor actor)
    {
        if (cursor is null) return null;
        if (cursor.Length is < 1 or > 512 || cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw InvalidQuery();
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            var position = JsonSerializer.Deserialize<Position>(Convert.FromBase64String(encoded), CursorJson);
            if (position is null || position.Version != 1 || position.ProjectId == Guid.Empty ||
                position.ActorId != actor.UserId || position.Administrator != actor.IsAdministrator ||
                Encode(position) != cursor) throw InvalidQuery();
            return position;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or OverflowException)
        { throw InvalidQuery(); }
    }

    private static RequestRejectedException InvalidQuery() => new(400, "InvalidProjectBrowseQuery");
}
// EOF
