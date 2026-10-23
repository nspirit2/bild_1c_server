using BuildSystem.Agent.Archives;
using Xunit;

namespace BuildSystem.ArchiveTests;

public sealed class StagingTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "archive-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid rootId = Guid.NewGuid();
    private string Root => Path.Combine(home, "source");
    private string Stage => Path.Combine(home, "stage");
    public StagingTests() { Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, "a.dt"), "content not a parsed dt"); }
    private ArchiveStaging Create(long max = 1024 * 1024, long total = 4 * 1024 * 1024) =>
        new([new(rootId, Root, true)], Stage, max, total);
    private async Task<StageRequest> Request(ArchiveStaging store) => new(Guid.NewGuid(), Guid.NewGuid(), await store.FingerprintAsync(rootId, "a.dt"));

    [Fact] public async Task NormalContentVerifiedAndSourceNeverRemoved()
    {
        var store = Create(); var request = await Request(store);
        var artifact = await store.StageAsync(request);
        Assert.Equal(ArchiveFormat.Dt, artifact.Format);
        Assert.Equal(request.Fingerprint.Sha256, artifact.Sha256);
        Assert.True(File.Exists(Path.Combine(Root, "a.dt")));
        Assert.DoesNotContain(home, artifact.ToString());
        var manifest = await File.ReadAllTextAsync(Path.Combine(Stage, artifact.ArtifactId, "manifest.json"));
        Assert.DoesNotContain(Root, manifest); Assert.DoesNotContain("a.dt", manifest);
    }

    [Fact] public async Task ReplacementAfterFingerprintIsChangedAndCleansTemp()
    {
        var store = Create(); var request = await Request(store);
        File.WriteAllText(Path.Combine(Root, "a.dt"), "different same selected path");
        var ex = await Assert.ThrowsAsync<ArchiveException>(() => store.StageAsync(request));
        Assert.Equal(ArchiveFailure.ArchiveChanged, ex.Failure);
        Assert.Empty(Directory.EnumerateDirectories(Stage, ".tmp-*"));
    }

    [Fact] public async Task ConcurrentIdempotencyAndCrossInstanceRetry()
    {
        var store = Create(); var request = await Request(store);
        var artifacts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.StageAsync(request)));
        Assert.Single(artifacts.Select(a => a.ArtifactId).Distinct());
        Assert.Equal(artifacts[0].ArtifactId, (await Create().StageAsync(request)).ArtifactId);
        Assert.Single(Directory.EnumerateDirectories(Stage));
    }

    [Fact] public async Task CorruptExistingArtifactNeverOverwritten()
    {
        var store = Create(); var request = await Request(store); var artifact = await store.StageAsync(request);
        var data = Path.Combine(Stage, artifact.ArtifactId, "data");
        await File.WriteAllTextAsync(data, "corrupt");
        Assert.Equal(ArchiveFailure.ArchiveInvalid, (await Assert.ThrowsAsync<ArchiveException>(() => store.StageAsync(request))).Failure);
        Assert.Equal("corrupt", await File.ReadAllTextAsync(data));
    }

    [Fact] public async Task SamePairDifferentMetadataIsConflict()
    {
        var store = Create(); var request = await Request(store); await store.StageAsync(request);
        request = request with { Fingerprint = request.Fingerprint with { Sha256 = new string('0', 64) } };
        Assert.Equal(ArchiveFailure.ArchiveChanged, (await Assert.ThrowsAsync<ArchiveException>(() => store.StageAsync(request))).Failure);
    }

    [Fact] public async Task WrongPinnedHashFails()
    {
        var store = Create(); var request = await Request(store);
        request = request with { Fingerprint = request.Fingerprint with { Sha256 = new string('0', 64) } };
        Assert.Equal(ArchiveFailure.ArchiveChanged, (await Assert.ThrowsAsync<ArchiveException>(() => store.StageAsync(request))).Failure);
    }

    [Theory]
    [InlineData("../a.dt")][InlineData("/a.dt")][InlineData("a\\b.dt")][InlineData("a.dt:stream")]
    [InlineData("CON.dt")][InlineData("COM1.dt")][InlineData("a//b.dt")][InlineData("a/./b.dt")]
    [InlineData("a./b.dt")][InlineData("CLOCK$.dt")][InlineData("COM¹.dt")]
    public async Task UnsafeNamesRejected(string name) => await Assert.ThrowsAsync<ArgumentException>(() => Create().FingerprintAsync(rootId, name));

    [Fact] public async Task UnsupportedAndEmptyAreNotFingerprints()
    {
        var store = Create(); File.WriteAllText(Path.Combine(Root, "a.zip"), "not supported");
        Assert.Equal(ArchiveFailure.ArchiveFormatUnsupported, (await Assert.ThrowsAsync<ArchiveException>(() => store.FingerprintAsync(rootId, "a.zip"))).Failure);
        File.WriteAllText(Path.Combine(Root, "empty.bak"), "");
        Assert.Equal(ArchiveFailure.ArchiveInvalid, (await Assert.ThrowsAsync<ArchiveException>(() => store.FingerprintAsync(rootId, "empty.bak"))).Failure);
    }

    [Fact] public void OperatorPolicyAndOverlapRequired()
    {
        Assert.Throws<ArgumentException>(() => new ArchiveStaging([new(rootId, Root, false)], Stage, 10, 100));
        Assert.Throws<ArgumentException>(() => new ArchiveStaging([new(rootId, Root, true)], Path.Combine(Root, "stage"), 10, 100));
        Assert.Throws<ArgumentException>(() => new ArchiveStaging([new(rootId, Path.GetPathRoot(Root)!, true)], Stage, 10, 100));
    }

    [Fact] public async Task ReparseSourceAndStagingRejected()
    {
        // Windows symlink creation requires developer mode or privilege; junction/symlink policy is identical.
        if (OperatingSystem.IsWindows()) return;
        var link = Path.Combine(Root, "link.dt"); File.CreateSymbolicLink(link, Path.Combine(Root, "a.dt"));
        await Assert.ThrowsAsync<ArchiveException>(() => Create().FingerprintAsync(rootId, "link.dt"));
        Directory.CreateDirectory(Stage); Directory.CreateSymbolicLink(Path.Combine(Stage, "evil"), Root);
        var store = Create(); var request = await Request(store);
        await Assert.ThrowsAsync<ArchiveException>(() => store.StageAsync(request));
    }

    [Fact] public async Task AbandonedTempIgnoredButRetainedAndQuotaCharged()
    {
        var store = Create(total: 1024); var request = await Request(store);
        var temp = Path.Combine(Stage, ".tmp-crash"); Directory.CreateDirectory(temp);
        await File.WriteAllBytesAsync(Path.Combine(temp, "data"), new byte[1000]);
        Assert.Equal(ArchiveFailure.ArchiveQuotaExceeded, (await Assert.ThrowsAsync<ArchiveException>(() => store.StageAsync(request))).Failure);
        Assert.True(Directory.Exists(temp));
        var artifact = await Create().StageAsync(request);
        Assert.True(Directory.Exists(Path.Combine(Stage, artifact.ArtifactId)));
        Assert.True(Directory.Exists(temp));
    }

    [Fact] public async Task LimitsAndCancellation()
    {
        Assert.Equal(ArchiveFailure.ArchiveQuotaExceeded, (await Assert.ThrowsAsync<ArchiveException>(() => Create(max: 2).FingerprintAsync(rootId, "a.dt"))).Failure);
        var store = Create(total: 1); var request = await Request(store);
        Assert.Equal(ArchiveFailure.ArchiveQuotaExceeded, (await Assert.ThrowsAsync<ArchiveException>(() => store.StageAsync(request))).Failure);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().StageAsync(request, cts.Token));
        Assert.Empty(Directory.EnumerateDirectories(Stage));
    }

    [Fact] public async Task CancellationWhileWaitingForExternalLockDoesNotPublish()
    {
        var store = Create(); var request = await Request(store);
        using var external = new FileStream(Path.Combine(Stage, ".stage.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StageAsync(request, cts.Token));
        Assert.Empty(Directory.EnumerateDirectories(Stage));
    }

    public void Dispose() { if (Directory.Exists(home)) Directory.Delete(home, true); }
}
