using BuildSystem.Agent;
using BuildSystem.Agent.Archives;
using BuildSystem.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;
namespace BuildSystem.AgentTests;

public sealed class CleanupResultDeliveryTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "cleanup-result-outbox", Guid.NewGuid().ToString("N"));
    private readonly Guid rootId = Guid.NewGuid();
    private AgentJournal journal;
    private readonly ArchiveStaging staging;
    private StageCleanupAuthorization grant = null!;
    private string ArtifactDirectory => Path.Combine(home, "staging", grant.Plan.ArtifactId.ToString("N"));
    public CleanupResultDeliveryTests()
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
    private sealed class Transport : ICleanupResultTransport
    {
        public int Calls; public Func<ReportStageCleanup, CancellationToken, Task<StageCleanupResultReceipt>> Send = (r, _) => Task.FromResult(new StageCleanupResultReceipt(r.ResultId, r.AuthorizationId, r.ArtifactId, "DeletedReported", DateTimeOffset.UtcNow));
        public Task<StageCleanupResultReceipt> ReportCleanupResultAsync(ReportStageCleanup r, CancellationToken ct) { Calls++; return Send(r, ct); }
    }
    [Fact]
    public async Task EmptyDoesNotCallTransport()
    {
        var t = new Transport(); Assert.False(await new CleanupResultDelivery(journal,t).DeliverOneAsync()); Assert.Equal(0,t.Calls);
    }
    [Fact]
    public async Task ExactReceiptAcksOnceAndReopenDoesNotResend()
    {
        await Prepare(); await Execute(); var r=Assert.Single(journal.PendingCleanupResults()); var t=new Transport();
        var delivery=new CleanupResultDelivery(journal,t); Assert.True(await delivery.DeliverOneAsync()); Assert.False(await delivery.DeliverOneAsync());
        Reopen(); Assert.False(await new CleanupResultDelivery(journal,t).DeliverOneAsync()); Assert.Equal(1,t.Calls); Assert.Equal("Acked",journal.GetCleanupResultState(r.ResultId));
        Assert.True(File.Exists(Path.Combine(home,"source","sample.dt")));
    }
    [Theory]
    [InlineData(400,true)] [InlineData(409,true)] [InlineData(401,false)] [InlineData(403,false)] [InlineData(404,false)] [InlineData(429,false)] [InlineData(500,false)] [InlineData(503,false)]
    public async Task HttpPolicyPreservesRetryableIdentityAndBlocksOnlyDefinitiveConflict(int status,bool blocked)
    {
        await Prepare(); await Execute(); var r=Assert.Single(journal.PendingCleanupResults());
        var t=new Transport { Send=(_,_)=>throw new System.Net.Http.HttpRequestException("safe",null,(System.Net.HttpStatusCode)status) };
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(()=>new CleanupResultDelivery(journal,t).DeliverOneAsync());
        Assert.Equal(blocked?"Blocked":"Pending",journal.GetCleanupResultState(r.ResultId));
        Reopen(); if(!blocked) Assert.Equal(r,Assert.Single(journal.PendingCleanupResults())); else Assert.Empty(journal.PendingCleanupResults());
    }
    [Theory]
    [InlineData("result")] [InlineData("authorization")] [InlineData("artifact")] [InlineData("state")] [InlineData("date")] [InlineData("null")]
    public async Task AlternateTransportMismatchBlocksWithoutAck(string field)
    {
        await Prepare(); await Execute(); var r=Assert.Single(journal.PendingCleanupResults()); var receipt=Receipt(r);
        receipt=field switch { "result"=>receipt with{ResultId=Guid.NewGuid()},"authorization"=>receipt with{AuthorizationId=Guid.NewGuid()},"artifact"=>receipt with{ArtifactId=Guid.NewGuid()},"state"=>receipt with{State="DeletedLocal"},"date"=>receipt with{ReceivedAt=default},_=>null!};
        var t=new Transport{Send=(_,_)=>Task.FromResult(receipt)};
        await Assert.ThrowsAsync<CleanupResultReceiptException>(()=>new CleanupResultDelivery(journal,t).DeliverOneAsync());
        Assert.Equal("Blocked",journal.GetCleanupResultState(r.ResultId));
    }
    [Fact]
    public async Task NetworkFailureRetriesSameResultAndThenAcks()
    {
        await Prepare(); await Execute(); var r=Assert.Single(journal.PendingCleanupResults()); var t=new Transport{Send=(_,_)=>throw new System.Net.Http.HttpRequestException("network")};
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(()=>new CleanupResultDelivery(journal,t).DeliverOneAsync()); Reopen(); Assert.Equal(r,Assert.Single(journal.PendingCleanupResults()));
        var good=new Transport(); Assert.True(await new CleanupResultDelivery(journal,good).DeliverOneAsync()); Assert.Equal("Acked",journal.GetCleanupResultState(r.ResultId));
    }
    [Fact]
    public async Task CancellationBeforeOrDuringIoDoesNotBlock()
    {
        await Prepare(); await Execute(); var r=Assert.Single(journal.PendingCleanupResults()); var t=new Transport(); using var c=new CancellationTokenSource(); c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new CleanupResultDelivery(journal,t).DeliverOneAsync(c.Token)); Assert.Equal(0,t.Calls);
        t.Send=(_,_)=>throw new TaskCanceledException(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new CleanupResultDelivery(journal,t).DeliverOneAsync());
        Assert.Equal(r,Assert.Single(journal.PendingCleanupResults()));
    }
    [Fact]
    public async Task OneCallDeliversOnlyOldestPendingWithoutDeletingAnotherArtifact()
    {
        await Prepare(); await Execute(); var first=Assert.Single(journal.PendingCleanupResults());
        await Prepare(); await Execute(); var second=journal.PendingCleanupResults()[1]; var seen=new List<Guid>();
        var t=new Transport{Send=(r,_)=>{seen.Add(r.ResultId);return Task.FromResult(Receipt(r));}};
        Assert.True(await new CleanupResultDelivery(journal,t).DeliverOneAsync());
        Assert.Equal(new[]{first.ResultId},seen); Assert.Equal(second,Assert.Single(journal.PendingCleanupResults()));
        Assert.Equal("Acked",journal.GetCleanupResultState(first.ResultId)); Assert.True(File.Exists(Path.Combine(home,"source","sample.dt")));
    }
    [Fact]
    public async Task MalformedTransportReceiptExceptionBlocksAndIsNotRetried()
    {
        await Prepare(); await Execute(); var r=Assert.Single(journal.PendingCleanupResults());
        var t=new Transport{Send=(_,_)=>throw new CleanupResultReceiptException()}; var d=new CleanupResultDelivery(journal,t);
        await Assert.ThrowsAsync<CleanupResultReceiptException>(()=>d.DeliverOneAsync()); Assert.False(await d.DeliverOneAsync());
        Assert.Equal(1,t.Calls); Assert.Equal("Blocked",journal.GetCleanupResultState(r.ResultId));
    }
    public void Dispose() { journal.Dispose(); if(Directory.Exists(home)) Directory.Delete(home,true); }
}
