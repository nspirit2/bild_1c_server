using BuildSystem.Agent;
using BuildSystem.Agent.Archives;
using BuildSystem.Contracts;
using Xunit;
namespace BuildSystem.AgentTests;

public sealed class StageCleanupCoordinatorTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "stage-cleanup-coordinator", Guid.NewGuid().ToString("N"));
    private readonly Guid rootId = Guid.NewGuid();
    private AgentJournal journal;
    private readonly ArchiveStaging staging;
    private readonly string source;
    private StageCleanupAuthorization grant = null!;
    private string ArtifactDirectory => Path.Combine(home, "staging", grant.Plan.ArtifactId.ToString("N"));
    public StageCleanupCoordinatorTests()
    {
        var root = Path.Combine(home, "source"); Directory.CreateDirectory(root);
        source = Path.Combine(root, "sample.dt"); File.WriteAllText(source, "SYNTHETIC NOT A VALID 1C ARCHIVE");
        staging = new([new(rootId, root, true)], Path.Combine(home, "staging"), 1024 * 1024, 1024 * 1024);
        journal = new(Path.Combine(home, "journal"));
    }
    private async Task Prepare(bool ackCompletion = true, bool storeAuthorization = true)
    {
        var reservation = Guid.NewGuid(); var version = Guid.NewGuid(); var job = Guid.NewGuid();
        var fp = await staging.FingerprintAsync(rootId, "sample.dt");
        var artifact = await staging.StageAsync(new(reservation, version, fp));
        var format = fp.Format == ArchiveFormat.Dt ? "dt" : "bak";
        grant = new(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), job, Guid.Parse(artifact.ArtifactId), reservation, version, 1, fp.Sha256.ToUpperInvariant(), fp.Size, format), DateTimeOffset.UtcNow);
        var p = grant.Plan;
        journal.StoreGrant(new(job, 1, DateTimeOffset.UtcNow.AddMinutes(2), "ArchiveStageOnly", "{}"));
        journal.SaveStageIntent(new(job, 1, reservation, version, rootId, "sample.dt", fp.Sha256, fp.Size, format));
        journal.EnqueueArtifact(new(job, 1, reservation, version, p.ArtifactId, fp.Sha256, fp.Size, format));
        journal.AcknowledgeArtifact(p.ArtifactId); journal.EnqueueCompletion(new(job, 1, p.ArtifactId));
        if (ackCompletion) journal.AcknowledgeCompletion(new(job, 1, p.ArtifactId, "StageCompleted"));
        if (storeAuthorization) journal.StoreCleanupAuthorization(grant);
    }
    private Task Execute(CancellationToken ct = default) => new StageCleanupCoordinator(journal, staging).ExecuteAsync(grant.AuthorizationId, ct);
    private void Reopen() { journal.Dispose(); journal = new(Path.Combine(home, "journal")); }

    [Fact]
    public async Task FirstExecutionDeletesOnlyStagedArtifactAndPersistsLocalResult()
    {
        await Prepare(); var original = File.ReadAllText(source); await Execute();
        Assert.Equal("DeletedLocal", journal.GetCleanupLocalState(grant.AuthorizationId));
        Assert.False(Directory.Exists(ArtifactDirectory)); Assert.Equal(original, File.ReadAllText(source));
        Assert.Empty(journal.PendingCleanupAuthorizations());
        Assert.Equal("Acked", journal.ArtifactState(grant.Plan.ArtifactId));
        Assert.Equal("Acked", journal.CompletionState(grant.Plan.JobId, 1));
    }
    [Fact]
    public async Task UnknownAuthorizationDoesNotTouchBytes()
    {
        await Prepare(); await Assert.ThrowsAsync<InvalidOperationException>(() => new StageCleanupCoordinator(journal, staging).ExecuteAsync(Guid.NewGuid()));
        Assert.True(File.Exists(Path.Combine(ArtifactDirectory, "data")));
        Assert.Null(journal.GetCleanupLocalState(grant.AuthorizationId));
    }
    [Fact]
    public async Task MissingCompletionCannotPrepareOrDelete()
    {
        await Prepare(ackCompletion: false, storeAuthorization: false); journal.MarkSettled(grant.Plan.JobId, 1);
        Assert.Throws<InvalidOperationException>(() => journal.StoreCleanupAuthorization(grant));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute());
        Assert.True(Directory.Exists(ArtifactDirectory));
    }
    [Fact]
    public async Task OtherUnsettledLeaseBlocksExecutionBeforeDeletingState()
    {
        await Prepare(); journal.StoreGrant(new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(1), "ControlPlaneProbe", "{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute());
        Assert.Null(journal.GetCleanupLocalState(grant.AuthorizationId)); Assert.True(Directory.Exists(ArtifactDirectory));
    }
    [Fact]
    public async Task ReopenDeletingAfterCancellationCanRetryExactAuthority()
    {
        await Prepare(); journal.BeginCleanupExecution(grant.AuthorizationId);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(cancel.Token));
        Reopen(); Assert.Equal("Deleting", journal.GetCleanupLocalState(grant.AuthorizationId));
        await Execute(); Assert.Equal("DeletedLocal", journal.GetCleanupLocalState(grant.AuthorizationId));
    }
    [Fact]
    public async Task ExactDeletedReplayVerifiesMarkerAndRetainsSameIdentity()
    {
        await Prepare(); await Execute(); Reopen(); await Execute();
        Assert.Equal(grant, journal.GetCleanupAuthorization(grant.AuthorizationId));
        Assert.Equal("DeletedLocal", journal.GetCleanupLocalState(grant.AuthorizationId));
        Assert.False(Directory.Exists(ArtifactDirectory)); Assert.True(File.Exists(source));
    }
    [Fact]
    public async Task RecreatedArtifactAfterDeletedLocalIsNotSilentlyAccepted()
    {
        await Prepare(); await Execute(); Directory.CreateDirectory(ArtifactDirectory);
        File.WriteAllText(Path.Combine(ArtifactDirectory, "data"), "new unrelated bytes");
        await Assert.ThrowsAnyAsync<Exception>(() => Execute());
        Assert.Equal("new unrelated bytes", File.ReadAllText(Path.Combine(ArtifactDirectory, "data")));
    }
    [Fact]
    public async Task TamperedManifestLeavesDeletingAndDoesNotDeleteBytes()
    {
        await Prepare(); var manifest = Path.Combine(ArtifactDirectory, "manifest.json"); File.WriteAllText(manifest, "{}");
        await Assert.ThrowsAnyAsync<Exception>(() => Execute());
        Assert.Equal("Deleting", journal.GetCleanupLocalState(grant.AuthorizationId));
        Assert.True(File.Exists(Path.Combine(ArtifactDirectory, "data"))); Assert.True(File.Exists(source));
    }
    [Fact]
    public async Task MissingArtifactWithoutEngineIntentIsNotLocalSuccess()
    {
        await Prepare();
        // This test owns this exact temporary artifact directory; simulate externally lost bytes.
        Assert.StartsWith(Path.GetFullPath(home) + Path.DirectorySeparatorChar, Path.GetFullPath(ArtifactDirectory), StringComparison.Ordinal);
        Directory.Delete(ArtifactDirectory, true);
        await Assert.ThrowsAnyAsync<Exception>(() => Execute());
        Assert.Equal("Deleting", journal.GetCleanupLocalState(grant.AuthorizationId)); Assert.True(File.Exists(source));
    }
    [Fact]
    public async Task TamperedDataHashLeavesDeletingAndPreservesEvidence()
    {
        await Prepare(); File.WriteAllText(Path.Combine(ArtifactDirectory, "data"), "changed");
        await Assert.ThrowsAnyAsync<Exception>(() => Execute());
        Assert.Equal("Deleting", journal.GetCleanupLocalState(grant.AuthorizationId));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(ArtifactDirectory, "data")));
    }
    public void Dispose()
    {
        journal.Dispose();
        var resolved = Path.GetFullPath(home);
        var expected = Path.Combine(Path.GetTempPath(), "stage-cleanup-coordinator") + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(expected, StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
// end
