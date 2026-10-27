namespace BuildSystem.Application;

/// <summary>Bounded project catalog request. Cursor is an opaque position, not an authorization token.</summary>
public sealed record ArchiveCatalogQuery(int Limit = 25, string? Cursor = null);

/// <summary>Selection metadata only; never a claim that a source file is currently available.</summary>
public sealed record ArchiveCatalogView(Guid ArchiveVersionId, string Format, long SizeBytes, long EstimatedDbBytes);

public sealed record ArchiveCatalogPage(IReadOnlyList<ArchiveCatalogView> Items, string? NextCursor,
    DateTimeOffset ObservedAt);

public interface IArchiveCatalogBrowse
{
    Task<ArchiveCatalogPage> ListAsync(Actor actor, Guid projectId, ArchiveCatalogQuery query,
        CancellationToken ct = default);
}
// EOF
