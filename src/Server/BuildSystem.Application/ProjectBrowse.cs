namespace BuildSystem.Application;

/// <summary>Bounded project selection metadata; a display name is not execution authority.</summary>
public sealed record ProjectBrowseView(Guid ProjectId, string Name);
public sealed record ProjectBrowseQuery(int Limit = 25, string? Cursor = null);
public sealed record ProjectBrowsePage(IReadOnlyList<ProjectBrowseView> Items, string? NextCursor, DateTimeOffset ObservedAt);

public interface IProjectBrowse
{
    Task<ProjectBrowsePage> ListAsync(Actor actor, ProjectBrowseQuery query, CancellationToken ct = default);
}
// EOF
