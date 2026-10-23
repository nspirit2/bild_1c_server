namespace BuildSystem.Application;

/// <summary>Partial inventory only. Pass the raw bearer token, not an agent ID authenticated earlier.
/// File completion, reparse-point containment and fingerprint stability are the local scanner's responsibility.
/// Inventory is not format validation: discovered versions are not eligible for orders.</summary>
public interface IArchiveCatalog
{
    Task<ArchiveInventoryReceipt> ReportAsync(string credential, ArchiveInventoryReport report, CancellationToken ct);
}

public sealed record ArchiveInventoryReport(Guid RootId, ArchiveInventoryEntry[] Entries);
public sealed record ArchiveInventoryEntry(string RelativePath, string Sha256, long SizeBytes, string Format);
public sealed record ArchiveInventoryReceipt(Guid RootId, ArchiveInventoryItemReceipt[] Entries);
public sealed record ArchiveInventoryItemReceipt(Guid ArchiveId, Guid ArchiveVersionId, string State);
