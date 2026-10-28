using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BuildSystem.ArchiveTests")]
namespace BuildSystem.Agent.Archives;

public sealed record ArchiveCleanupRequest(Guid AuthorizationId, Guid ArtifactId, Guid ReservationId,
    Guid VersionId, Guid RootId, string SourceNameHash, string Sha256, long Size, ArchiveFormat Format);

public sealed partial class ArchiveStaging
{
    // Fault injection only. The caller still owns journal/authority verification before invoking this engine.
    internal Action<string>? CleanupCheckpoint { get; set; }
    private static readonly JsonSerializerOptions CleanupJson = new()
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Deletes only a pinned staging artifact, never source files. Requires protected staging ACLs
    /// excluding hostile concurrent writers; reparse checks and cooperating-process locks are not a defense
    /// against a malicious administrator racing pathname operations. Permanent intent/completion markers
    /// prevent identity resurrection through this engine; this is not quota release.</summary>
    public async Task CleanupAsync(ArchiveCleanupRequest request, CancellationToken ct = default)
    {
        if (request is null || request.AuthorizationId == Guid.Empty || request.ArtifactId == Guid.Empty ||
            request.ReservationId == Guid.Empty || request.VersionId == Guid.Empty || request.RootId == Guid.Empty ||
            !IsSha(request.Sha256) || !IsSha(request.SourceNameHash) || request.Size <= 0 ||
            !Enum.IsDefined(request.Format)) throw new ArgumentException("Invalid cleanup identity.", nameof(request));
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes($"archive-stage-v1:{request.ReservationId:N}:{request.VersionId:N}"));
        identity[6] = (byte)((identity[6] & 0x0f) | 0x80); identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
        if (new Guid(identity.AsSpan(0, 16), bigEndian: true) != request.ArtifactId)
            throw new ArgumentException("Invalid cleanup artifact identity.", nameof(request));
        request = request with { Sha256 = request.Sha256.ToLowerInvariant(), SourceNameHash = request.SourceNameHash.ToLowerInvariant() };
        var id = request.ArtifactId.ToString("N");
        var final = Path.Combine(staging, id);
        var intent = Path.Combine(staging, ".retired-" + id + ".json");
        var complete = Path.Combine(staging, ".deleted-" + id + ".json");
        var expected = new Manifest(request.ReservationId, request.VersionId, request.RootId,
            request.SourceNameHash, request.Sha256, request.Size, request.Format);
        await mutex.WaitAsync(ct);
        try
        {
            RejectReparseAncestors(staging);
            using var lifetime = await LockAsync(ct);
            foreach (var p in new[] { final, intent, complete }) RejectReparseAncestors(p);
            bool hasIntent = File.Exists(intent);
            if (Directory.Exists(intent) || Directory.Exists(complete) || File.Exists(final)) throw InvalidCleanup();
            if (Directory.EnumerateFileSystemEntries(staging, Path.GetFileName(intent) + ".pending-*", SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFileSystemEntries(staging, Path.GetFileName(complete) + ".pending-*", SearchOption.TopDirectoryOnly).Any())
                throw InvalidCleanup(); // Crash orphan needs operator inspection, not silent deletion.
            if (hasIntent) await CheckMarker(intent, request, ct);
            if (File.Exists(complete))
            {
                if (!hasIntent) throw InvalidCleanup();
                await CheckMarker(complete, request, ct);
                if (Directory.Exists(final)) throw InvalidCleanup(); // No deleting a resurrected incarnation.
                return;
            }
            if (!hasIntent)
            {
                if (!Directory.Exists(final)) throw InvalidCleanup(); // Absence without durable intent is not success.
                await VerifyRemaining(final, expected, requireBoth: true, ct);
                Checkpoint("before-intent", ct);
                WriteMarker(intent, request);
                Checkpoint("after-intent", ct);
            }
            if (Directory.Exists(final))
            {
                await VerifyRemaining(final, expected, requireBoth: false, ct);
                var data = Path.Combine(final, "data");
                var manifest = Path.Combine(final, "manifest.json");
                // Only the two exact allowlisted files; never recursive deletion.
                if (File.Exists(data)) { RejectReparseAncestors(data); File.Delete(data); }
                Checkpoint("after-data", ct);
                if (File.Exists(manifest)) { RejectReparseAncestors(manifest); File.Delete(manifest); }
                Checkpoint("after-manifest", ct);
                RejectReparseAncestors(final);
                Directory.Delete(final, recursive: false);
            }
            Checkpoint("after-directory", ct);
            WriteMarker(complete, request);
            Checkpoint("after-completed", ct);
        }
        finally { mutex.Release(); }
    }

    private void Checkpoint(string phase, CancellationToken ct) { CleanupCheckpoint?.Invoke(phase); ct.ThrowIfCancellationRequested(); }
    private static ArchiveException InvalidCleanup() => new(ArchiveFailure.ArchiveInvalid, "Cleanup identity or storage is inconsistent");
    private static void WriteMarker(string path, ArchiveCleanupRequest request)
    {
        RejectReparseAncestors(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, CleanupJson);
        var temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        // Same-directory publication: a process crash may leave charged orphan temp bytes, never partial intent.
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, overwrite: false);
    }
    private static async Task CheckMarker(string path, ArchiveCleanupRequest expected, CancellationToken ct)
    {
        RejectReparseAncestors(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 16384) throw InvalidCleanup();
            var actual = await JsonSerializer.DeserializeAsync<ArchiveCleanupRequest>(stream, CleanupJson, ct);
            if (actual != expected) throw InvalidCleanup();
        }
        catch (JsonException) { throw InvalidCleanup(); }
    }
    private async Task VerifyRemaining(string directory, Manifest expected, bool requireBoth, CancellationToken ct)
    {
        RejectReparseAncestors(directory);
        var entries = Directory.GetFileSystemEntries(directory);
        if (entries.Length > 2) throw InvalidCleanup();
        foreach (var entry in entries)
        {
            RejectReparseAncestors(entry);
            if (Directory.Exists(entry) || Path.GetFileName(entry) is not ("data" or "manifest.json")) throw InvalidCleanup();
        }
        var manifest = Path.Combine(directory, "manifest.json"); var data = Path.Combine(directory, "data");
        if (requireBoth && (!File.Exists(manifest) || !File.Exists(data))) throw InvalidCleanup();
        if (File.Exists(manifest))
        {
            using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > 16384) throw InvalidCleanup();
            try { if (await JsonSerializer.DeserializeAsync<Manifest>(stream, CleanupJson, ct) != expected) throw InvalidCleanup(); }
            catch (JsonException) { throw InvalidCleanup(); }
        }
        if (File.Exists(data))
        {
            await using var stream = new FileStream(data, FileMode.Open, FileAccess.Read, FileShare.Read);
            var (hash, size) = await HashAsync(stream, null, ct, expected.Size);
            if (size != expected.Size || hash != expected.Sha256) throw InvalidCleanup();
        }
    }
}
// end
