namespace BuildSystem.Application;

/// <summary>Bounded visible environment history; cursors do not grant authority.</summary>
public sealed record EnvironmentBrowseQuery(int Limit = 25, string? Cursor = null);
public sealed record EnvironmentBrowsePage(IReadOnlyList<EnvironmentView> Items, string? NextCursor, DateTimeOffset ObservedAt);

public interface IEnvironmentBrowse
{
    Task<EnvironmentBrowsePage> ListAsync(Actor actor, EnvironmentBrowseQuery query, CancellationToken ct = default);
}
// EOF
