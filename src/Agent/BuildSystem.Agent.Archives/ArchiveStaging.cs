using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BuildSystem.Agent.Archives;

public enum ArchiveFailure { ArchiveUnavailable, ArchiveChanged, ArchiveInvalid, ArchiveFormatUnsupported, ArchiveQuotaExceeded }

public sealed class ArchiveException(ArchiveFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public ArchiveFailure Failure { get; } = failure;
}

public enum ArchiveFormat { Dt, Bak }

// AtomicPublication is an operator assertion about the producer, not a freshness heuristic.
public sealed record ArchiveRoot(Guid Id, string AbsolutePath, bool AtomicPublication);
public sealed record SourceFingerprint(Guid RootId, string RelativePath, string Sha256, long Size, ArchiveFormat Format);
public sealed record StageRequest(Guid ReservationId, Guid VersionId, SourceFingerprint Fingerprint);

// Content-verified only: no .dt parser, SQL VERIFYONLY, or restore validation is performed here.
public sealed class VerifiedArtifact
{
    internal VerifiedArtifact(string id, string path, ArchiveFormat format, long size, string sha256)
    { ArtifactId = id; InternalPath = path; Format = format; Size = size; Sha256 = sha256; }
    public string ArtifactId { get; }
    public ArchiveFormat Format { get; }
    public long Size { get; }
    public string Sha256 { get; }
    internal string InternalPath { get; }
    public override string ToString() => $"VerifiedArtifact({ArtifactId})";
}

public sealed class ArchiveStaging
{
    private readonly Dictionary<Guid, string> roots;
    private readonly string staging;
    private readonly long maxArchiveBytes, maxStagingBytes;
    private readonly SemaphoreSlim mutex = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };
    private sealed record Manifest(Guid ReservationId, Guid VersionId, Guid RootId, string SourceNameHash,
        string Sha256, long Size, ArchiveFormat Format);

    public ArchiveStaging(IEnumerable<ArchiveRoot> configuredRoots, string stagingDirectory, long maxArchiveBytes, long maxStagingBytes)
    {
        if (maxArchiveBytes <= 0 || maxStagingBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxArchiveBytes));
        if (!Path.IsPathFullyQualified(stagingDirectory)) throw new ArgumentException("Staging directory must be absolute");
        staging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
        RejectReparseAncestors(staging);
        roots = new Dictionary<Guid, string>();
        foreach (var root in configuredRoots)
        {
            if (root.Id == Guid.Empty || !root.AtomicPublication || !Path.IsPathFullyQualified(root.AbsolutePath))
                throw new ArgumentException("Each root needs an ID, an absolute path and explicit atomic publication policy");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.AbsolutePath));
            RejectReparseAncestors(full);
            if (Overlaps(full, staging)) throw new ArgumentException("Staging directory overlaps a source root");
            if (!roots.TryAdd(root.Id, full)) throw new ArgumentException("Duplicate root ID");
        }
        if (roots.Count == 0) throw new ArgumentException("At least one source root is required");
        this.maxArchiveBytes = maxArchiveBytes;
        this.maxStagingBytes = maxStagingBytes;
        Directory.CreateDirectory(staging);
        RejectReparseAncestors(staging);
    }

    public async Task<SourceFingerprint> FingerprintAsync(Guid rootId, string relativePath, CancellationToken cancellationToken = default)
    {
        var (path, format) = Resolve(rootId, relativePath);
        try
        {
            await using var stream = OpenSource(path);
            var before = Snapshot(stream, path);
            var (hash, size) = await HashAsync(stream, null, cancellationToken);
            if (size == 0) throw new ArchiveException(ArchiveFailure.ArchiveInvalid, "Empty archive");
            CheckSnapshot(stream, path, before, size);
            return new SourceFingerprint(rootId, relativePath, hash, size, format);
        }
        catch (ArchiveException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { throw new ArchiveException(ArchiveFailure.ArchiveUnavailable, "Source cannot be read"); }
        catch (UnauthorizedAccessException) { throw new ArchiveException(ArchiveFailure.ArchiveUnavailable, "Source cannot be read"); }
    }

    public async Task<VerifiedArtifact> StageAsync(StageRequest request, CancellationToken cancellationToken = default)
    {
        try { return await StageCoreAsync(request, cancellationToken); }
        catch (IOException) { throw new ArchiveException(ArchiveFailure.ArchiveUnavailable, "Staging storage or source unavailable"); }
        catch (UnauthorizedAccessException) { throw new ArchiveException(ArchiveFailure.ArchiveUnavailable, "Staging storage or source unavailable"); }
    }

    private async Task<VerifiedArtifact> StageCoreAsync(StageRequest request, CancellationToken cancellationToken)
    {
        if (request.ReservationId == Guid.Empty || request.VersionId == Guid.Empty || request.Fingerprint is null)
            throw new ArgumentException("Reservation, version and fingerprint are required");
        var fingerprint = request.Fingerprint;
        var (source, format) = Resolve(fingerprint.RootId, fingerprint.RelativePath);
        if (format != fingerprint.Format || fingerprint.Size <= 0 || fingerprint.Size > maxArchiveBytes ||
            !IsSha(fingerprint.Sha256)) throw new ArgumentException("Invalid pinned fingerprint");
        var manifest = new Manifest(request.ReservationId, request.VersionId, fingerprint.RootId,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.RelativePath))).ToLowerInvariant(),
            fingerprint.Sha256.ToLowerInvariant(), fingerprint.Size, format);
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes($"archive-stage-v1:{request.ReservationId:N}:{request.VersionId:N}"));
        identity[6] = (byte)((identity[6] & 0x0f) | 0x80); // UUID v8, SHA-256 namespaced identity
        identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
        var id = new Guid(identity.AsSpan(0, 16), bigEndian: true).ToString("N");
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await mutex.WaitAsync(cancellationToken);
        try
        {
            // The OS lock serializes other processes as well as this instance; not a distributed/UNC lease.
            using var lifetime = await LockAsync(cancellationToken);
            var final = Path.Combine(staging, id);
            if (Directory.Exists(final)) return await VerifyExistingAsync(final, id, manifest, cancellationToken);
            if (File.Exists(final)) throw new ArchiveException(ArchiveFailure.ArchiveInvalid, "Artifact location is obstructed");
            var used = OccupiedBytes();
            if (fingerprint.Size > maxStagingBytes - used - manifestBytes.Length)
                throw new ArchiveException(ArchiveFailure.ArchiveQuotaExceeded, "Staging quota exceeded");
            var temp = Path.Combine(staging, ".tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var data = Path.Combine(temp, "data");
                await using (var input = OpenSource(source))
                {
                    var before = Snapshot(input, source);
                    await using (var output = new FileStream(data, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        var (hash, size) = await HashAsync(input, output, cancellationToken, fingerprint.Size);
                        CheckSnapshot(input, source, before, size);
                        if (size != fingerprint.Size || hash != manifest.Sha256)
                            throw new ArchiveException(ArchiveFailure.ArchiveChanged, "Source no longer matches selected version");
                        output.Flush(true);
                    }
                }
                // Manifest never stores source paths or credentials.
                using (var sidecar = new FileStream(Path.Combine(temp, "manifest.json"), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { sidecar.Write(manifestBytes); sidecar.Flush(true); }
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(temp, final); // same volume: directory becomes visible as a whole
                return new VerifiedArtifact(id, Path.Combine(final, "data"), format, fingerprint.Size, manifest.Sha256);
            }
            finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
        }
        finally { mutex.Release(); }
    }

    private async Task<VerifiedArtifact> VerifyExistingAsync(string final, string id, Manifest expected, CancellationToken token)
    {
        RejectReparseAncestors(final);
        var manifestPath = Path.Combine(final, "manifest.json");
        var data = Path.Combine(final, "data");
        RejectReparseAncestors(manifestPath);
        RejectReparseAncestors(data);
        Manifest? actual;
        try
        {
            if (new FileInfo(manifestPath).Length > 16384) throw new ArchiveException(ArchiveFailure.ArchiveInvalid, "Manifest too large");
            actual = JsonSerializer.Deserialize<Manifest>(await File.ReadAllBytesAsync(manifestPath, token), JsonOptions);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { throw new ArchiveException(ArchiveFailure.ArchiveInvalid, "Existing artifact manifest is invalid"); }
        if (actual != expected) throw new ArchiveException(ArchiveFailure.ArchiveChanged, "Reservation/version already pinned to another artifact");
        try
        {
            await using var stream = new FileStream(data, FileMode.Open, FileAccess.Read, FileShare.Read);
            var (hash, size) = await HashAsync(stream, null, token);
            if (hash != expected.Sha256 || size != expected.Size)
                throw new ArchiveException(ArchiveFailure.ArchiveInvalid, "Existing artifact content is corrupt");
        }
        catch (OperationCanceledException) { throw; }
        catch (ArchiveException) { throw; }
        catch (IOException) { throw new ArchiveException(ArchiveFailure.ArchiveInvalid, "Existing artifact unavailable"); }
        return new VerifiedArtifact(id, data, expected.Format, expected.Size, expected.Sha256);
    }

    private long OccupiedBytes()
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(staging))
        {
            RejectReparseAncestors(file);
            if (Path.GetFileName(file) != ".stage.lock") total = checked(total + new FileInfo(file).Length);
        }
        foreach (var dir in Directory.EnumerateDirectories(staging))
        {
            RejectReparseAncestors(dir);
            // Unknown directories and abandoned temp directories remain untouched and charge their byte size.
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
            {
                RejectReparseAncestors(file);
                total = checked(total + new FileInfo(file).Length);
            }
            if (Directory.EnumerateDirectories(dir).Any())
                throw new ArchiveException(ArchiveFailure.ArchiveInvalid, "Unexpected nested staging directory");
        }
        return total;
    }

    private async Task<FileStream> LockAsync(CancellationToken token)
    {
        var path = Path.Combine(staging, ".stage.lock");
        RejectReparseAncestors(path);
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                if (Environment.TickCount64 >= deadline) throw new ArchiveException(ArchiveFailure.ArchiveUnavailable, "Staging lock unavailable");
                await Task.Delay(40, token);
            }
        }
    }

    private (string Path, ArchiveFormat Format) Resolve(Guid root, string relative)
    {
        if (!roots.TryGetValue(root, out var basePath)) throw new ArchiveException(ArchiveFailure.ArchiveUnavailable, "Unknown source root");
        ValidateRelative(relative);
        var format = relative.EndsWith(".dt", StringComparison.OrdinalIgnoreCase) ? ArchiveFormat.Dt :
            relative.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? ArchiveFormat.Bak :
            throw new ArchiveException(ArchiveFailure.ArchiveFormatUnsupported, "Unsupported extension");
        RejectReparseAncestors(basePath);
        var path = Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar));
        RejectReparseAncestors(path);
        return (path, format);
    }

    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 400 || path.StartsWith('/') || path.Contains('\\') ||
            path.Any(c => char.IsControl(c) || c is ':' or '<' or '>' or '"' or '|' or '?' or '*'))
            throw new ArgumentException("Invalid canonical relative archive name");
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Length > 255)
                throw new ArgumentException("Invalid canonical relative archive segment");
            var stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is (>= '1' and <= '9') or '¹' or '²' or '³'))
                throw new ArgumentException("Windows reserved filename");
        }
    }

    private static bool IsSha(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static (long Length, DateTime LastWrite) Snapshot(FileStream stream, string path) => (stream.Length, File.GetLastWriteTimeUtc(path));
    private static void CheckSnapshot(FileStream stream, string path, (long Length, DateTime LastWrite) snapshot, long size)
    {
        if (snapshot.Length != size || stream.Length != snapshot.Length || File.GetLastWriteTimeUtc(path) != snapshot.LastWrite)
            throw new ArchiveException(ArchiveFailure.ArchiveChanged, "Source changed during reading");
    }
    private async Task<(string Hash, long Size)> HashAsync(Stream input, Stream? output, CancellationToken token, long? pinnedSize = null)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long count = 0;
        while (true)
        {
            int n = await input.ReadAsync(buffer, token);
            if (n == 0) break;
            count += n;
            if (count > maxArchiveBytes) throw new ArchiveException(ArchiveFailure.ArchiveQuotaExceeded, "Archive exceeds maximum size");
            if (pinnedSize is not null && count > pinnedSize)
                throw new ArchiveException(ArchiveFailure.ArchiveChanged, "Source exceeds selected size");
            sha.AppendData(buffer, 0, n);
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, n), token);
        }
        return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), count);
    }
    private static bool Overlaps(string a, string b) => Inside(a, b) || Inside(b, a);
    private static bool Inside(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith(Path.EndsInDirectorySeparator(b) ? b : b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RejectReparseAncestors(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
        {
            if (File.Exists(p) || Directory.Exists(p))
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new ArchiveException(ArchiveFailure.ArchiveUnavailable, "Reparse path is not allowed");
            }
            if (p == Path.GetPathRoot(p)) break;
        }
    }
}
