using BuildSystem.Agent;
using BuildSystem.Agent.Archives;
using BuildSystem.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;
namespace BuildSystem.AgentTests;

public sealed class CleanupResultOutboxTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "cleanup-result-outbox", Guid.NewGuid().ToString("N"));
    private readonly Guid rootId = Guid.NewGuid();
    private AgentJournal journal;
    private readonly ArchiveStaging staging;
    private StageCleanupAuthorization grant = null!;
    private string ArtifactDirectory => Path.Combine(home, "staging", grant.Plan.ArtifactId.ToString("N"));
    public CleanupResultOutboxTests()
    {
        Directory.CreateDirectory(Path.Combine(home, "source"));
        File.WriteAllText(Path.Combine(home, "source", "sample.dt"), "SYNTHETIC NOT A VALID 1C ARCHIVE");
        staging = new([new(rootId, Path.Combine(home, "source"), true)], Path.Combine(home, "staging"), 1048576, 1048576);
        journal = new(Path.Combine(home, "journal"));
    }
    private async Task Prepare()
    {
        var reservation = Guid.NewGuid(); var version = Guid.NewGuid(); var job = Guid.NewGuid();
        var fp = await staging.FingerprintAsync(rootId, "sample.dt");
        var artifact = await staging.StageAsync(new(reservation, version, fp));
        grant = new(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), job, Guid.Parse(artifact.ArtifactId), reservation, version, 1, fp.Sha256.ToUpperInvariant(), fp.Size, "dt"), DateTimeOffset.UtcNow);
        journal.StoreGrant(new(job, 1, DateTimeOffset.UtcNow.AddMinutes(2), "ArchiveStageOnly", "{}"));
        journal.SaveStageIntent(new(job, 1, reservation, version, rootId, "sample.dt", fp.Sha256, fp.Size, "dt"));
        journal.EnqueueArtifact(new(job, 1, reservation, version, grant.Plan.ArtifactId, fp.Sha256, fp.Size, "dt"));
        journal.AcknowledgeArtifact(grant.Plan.ArtifactId);
        journal.EnqueueCompletion(new(job, 1, grant.Plan.ArtifactId));
        journal.AcknowledgeCompletion(new(job, 1, grant.Plan.ArtifactId, "StageCompleted"));
        journal.StoreCleanupAuthorization(grant);
    }
    private Task Execute() => new StageCleanupCoordinator(journal, staging).ExecuteAsync(grant.AuthorizationId);
    private void Reopen() { journal.Dispose(); journal = new(Path.Combine(home, "journal")); }
    private StageCleanupResultReceipt Receipt(ReportStageCleanup result) => new(result.ResultId, result.AuthorizationId, result.ArtifactId, "DeletedReported", DateTimeOffset.UtcNow);
    [Fact]
    public async Task ExecuteAtomicallyCreatesOneExactAllowlistedResultAndKeepsSource()
    {
        await Prepare(); Assert.Empty(journal.PendingCleanupResults()); await Execute();
        var r = Assert.Single(journal.PendingCleanupResults()); var p = grant.Plan;
        Assert.NotEqual(Guid.Empty, r.ResultId); Assert.Equal(grant.AuthorizationId, r.AuthorizationId);
        Assert.Equal(p.RetirementId, r.RetirementId); Assert.Equal(p.JobId, r.JobId);
        Assert.Equal(p.ArtifactId, r.ArtifactId); Assert.Equal(p.FencingToken, r.FencingToken);
        Assert.Equal("DeletedLocal", journal.GetCleanupLocalState(grant.AuthorizationId));
        Assert.Equal("Pending", journal.GetCleanupResultState(r.ResultId));
        Assert.False(Directory.Exists(ArtifactDirectory));
        Assert.Equal("SYNTHETIC NOT A VALID 1C ARCHIVE", File.ReadAllText(Path.Combine(home, "source", "sample.dt")));
        Assert.Equal(6, typeof(ReportStageCleanup).GetProperties().Length);
    }
    [Fact]
    public async Task ReopenAndLocalReplayKeepSameResultIdentity()
    {
        await Prepare(); await Execute(); var original = Assert.Single(journal.PendingCleanupResults());
        Reopen(); Assert.Equal(original, Assert.Single(journal.PendingCleanupResults())); await Execute();
        Assert.Equal(original, Assert.Single(journal.PendingCleanupResults()));
    }
    [Fact]
    public async Task ExactAckReplayCanonicalizesUtcAndRemainsAckedAcrossLocalReplay()
    {
        await Prepare(); await Execute(); var r = Assert.Single(journal.PendingCleanupResults()); var receipt = Receipt(r);
        journal.AckCleanupResult(receipt); journal.AckCleanupResult(receipt with { ReceivedAt = receipt.ReceivedAt.ToOffset(TimeSpan.FromHours(3)) });
        Reopen(); await Execute(); journal.AckCleanupResult(receipt);
        Assert.Empty(journal.PendingCleanupResults()); Assert.Equal("Acked", journal.GetCleanupResultState(r.ResultId));
        Assert.Throws<InvalidOperationException>(() => journal.BlockCleanupResult(r.ResultId));
    }
    [Fact]
    public async Task ChangedAckTimestampRejectedWithoutChangingReceipt()
    {
        await Prepare(); await Execute(); var r = Assert.Single(journal.PendingCleanupResults()); var receipt = Receipt(r);
        journal.AckCleanupResult(receipt);
        Assert.Throws<InvalidOperationException>(() => journal.AckCleanupResult(receipt with { ReceivedAt = receipt.ReceivedAt.AddTicks(1) }));
        journal.AckCleanupResult(receipt); Assert.Equal("Acked", journal.GetCleanupResultState(r.ResultId));
    }
    [Theory]
    [InlineData("result")]
    [InlineData("authorization")]
    [InlineData("artifact")]
    [InlineData("state")]
    [InlineData("date")]
    public async Task WrongReceiptDoesNotAckOrChangePendingResult(string field)
    {
        await Prepare(); await Execute(); var r = Assert.Single(journal.PendingCleanupResults()); var receipt = Receipt(r);
        receipt = field switch { "result" => receipt with { ResultId = Guid.NewGuid() }, "authorization" => receipt with { AuthorizationId = Guid.NewGuid() }, "artifact" => receipt with { ArtifactId = Guid.NewGuid() }, "state" => receipt with { State = "DeletedLocal" }, _ => receipt with { ReceivedAt = default } };
        Assert.Throws<InvalidOperationException>(() => journal.AckCleanupResult(receipt));
        Assert.Equal(r, Assert.Single(journal.PendingCleanupResults())); Assert.Equal("Pending", journal.GetCleanupResultState(r.ResultId));
    }
    [Fact]
    public async Task BlockedIsFinalExcludedAndNotResetByLocalReplay()
    {
        await Prepare(); await Execute(); var r = Assert.Single(journal.PendingCleanupResults());
        journal.BlockCleanupResult(r.ResultId); journal.BlockCleanupResult(r.ResultId);
        Assert.Throws<InvalidOperationException>(() => journal.AckCleanupResult(Receipt(r)));
        Reopen(); await Execute(); Assert.Empty(journal.PendingCleanupResults()); Assert.Equal("Blocked", journal.GetCleanupResultState(r.ResultId));
        Assert.Throws<InvalidOperationException>(() => journal.BlockCleanupResult(Guid.NewGuid()));
    }
    [Fact]
    public async Task EngineFailureLeavesDeletingWithoutResult()
    {
        await Prepare(); File.WriteAllText(Path.Combine(ArtifactDirectory, "data"), "tamper");
        await Assert.ThrowsAnyAsync<Exception>(Execute);
        Assert.Equal("Deleting", journal.GetCleanupLocalState(grant.AuthorizationId));
        Assert.Empty(journal.PendingCleanupResults()); Reopen(); Assert.Empty(journal.PendingCleanupResults());
    }
    [Fact]
    public async Task PreparedOrManuallyBegunStateCannotPubliclyQueueResult()
    {
        await Prepare(); journal.BeginCleanupExecution(grant.AuthorizationId); Reopen();
        Assert.Empty(journal.PendingCleanupResults());
        Assert.DoesNotContain(typeof(AgentJournal).GetMethods(), m => m.Name == "MarkCleanupDeletedLocal" || m.Name == "StoreCleanupResult");
        await Execute(); Assert.Single(journal.PendingCleanupResults());
    }
    [Fact]
    public async Task LegacyDeletedLocalWithoutResultNeedsReverifiedCoordinatorReplay()
    {
        await Prepare(); await Execute(); journal.Dispose();
        // This test owns this exact isolated database: model upgrade from the previous version,
        // whose DeletedLocal records had no cleanup_results table or result identity.
        var path = Path.GetFullPath(Path.Combine(home, "journal", "agent-journal.sqlite"));
        Assert.StartsWith(Path.GetFullPath(home) + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "DELETE FROM cleanup_results"; command.ExecuteNonQuery();
        }
        journal = new(Path.Combine(home, "journal")); Assert.Empty(journal.PendingCleanupResults());
        await Execute(); Assert.Single(journal.PendingCleanupResults());
    }
    [Fact]
    public async Task FailedOutboxInsertRollsBackLocalSuccessAndReplaysAfterStorageRecovery()
    {
        await Prepare(); journal.Dispose();
        var path = Path.GetFullPath(Path.Combine(home, "journal", "agent-journal.sqlite"));
        Assert.StartsWith(Path.GetFullPath(home) + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        void StorageCommand(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        StorageCommand("CREATE TRIGGER reject_cleanup_result BEFORE INSERT ON cleanup_results BEGIN SELECT RAISE(ABORT,'simulated storage failure'); END");
        journal = new(Path.Combine(home, "journal"));
        await Assert.ThrowsAsync<SqliteException>(Execute);
        Assert.Equal("Deleting", journal.GetCleanupLocalState(grant.AuthorizationId));
        Assert.Empty(journal.PendingCleanupResults()); Assert.False(Directory.Exists(ArtifactDirectory));
        journal.Dispose(); StorageCommand("DROP TRIGGER reject_cleanup_result"); journal = new(Path.Combine(home, "journal"));
        await Execute(); Assert.Equal("DeletedLocal", journal.GetCleanupLocalState(grant.AuthorizationId));
        Assert.Single(journal.PendingCleanupResults());
    }
    public void Dispose()
    {
        journal.Dispose(); var resolved = Path.GetFullPath(home);
        if (!resolved.StartsWith(Path.Combine(Path.GetTempPath(), "cleanup-result-outbox") + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected cleanup path");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
// end
