using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Read-only bounded history; current visibility is checked on every page.</summary>
public sealed class SqlEnvironmentBrowse(BuildSystemDbContext db) : IEnvironmentBrowse
{
    private const string IdCollation = "Latin1_General_100_BIN2";
    private static readonly JsonSerializerOptions CursorJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record Position(int Version, Guid EnvironmentId, Guid ActorId, bool Administrator);

    public Task<EnvironmentBrowsePage> ListAsync(Actor actor, EnvironmentBrowseQuery query, CancellationToken ct = default)
    {
        if (actor is null || actor.UserId == Guid.Empty) throw new RequestRejectedException(403, "Forbidden");
        if (query is null || query.Limit is < 1 or > 100) throw InvalidQuery();
        var position = Decode(query.Cursor, actor);
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // Exactly the history visibility predicate in SqlEnvironmentOrders.Visible: no lifecycle/enabled filter.
            var rows = db.TestEnvironments.AsNoTracking().Where(environment => actor.IsAdministrator ||
                (environment.OwnerId == actor.UserId && db.ProjectMemberships.Any(m =>
                    m.UserId == actor.UserId && m.ProjectId == environment.ProjectId)));
            if (position is not null)
            {
                var id = position.EnvironmentId.ToString("D");
                rows = rows.Where(environment => string.Compare(EF.Functions.Collate(environment.Id.ToString().ToLower(), IdCollation), id) > 0);
            }
            // SQL uniqueidentifier ordering is different: the cursor and ORDER BY share lowercase GUID text BIN2.
            // Bound hostile/legacy display names in SQL before materialization, using UTF-16 code units.
            var items = await rows.OrderBy(environment => EF.Functions.Collate(environment.Id.ToString().ToLower(), IdCollation))
                .Select(environment => new EnvironmentView(environment.Id, environment.ProjectId,
                    EF.Functions.DataLength(environment.DisplayName) > 240
                        ? EF.Functions.Collate(environment.DisplayName, IdCollation).Substring(0, 120) : environment.DisplayName,
                    environment.State, environment.ExpiresAt))
                .Take(query.Limit + 1).ToListAsync(ct);
            var hasMore = items.Count > query.Limit;
            if (hasMore) items.RemoveAt(query.Limit);
            var next = hasMore ? Encode(new Position(1, items[^1].Id, actor.UserId, actor.IsAdministrator)) : null;
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
            await tx.CommitAsync(ct);
            return new EnvironmentBrowsePage(items, next, now);
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
            if (position is null || position.Version != 1 || position.EnvironmentId == Guid.Empty ||
                position.ActorId != actor.UserId || position.Administrator != actor.IsAdministrator ||
                Encode(position) != cursor) throw InvalidQuery();
            return position;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or OverflowException)
        { throw InvalidQuery(); }
    }

    private static RequestRejectedException InvalidQuery() => new(400, "InvalidEnvironmentBrowseQuery");
}
// EOF
