using BuildSystem.Agent;
using BuildSystem.Agent.Archives;
using BuildSystem.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;
namespace BuildSystem.AgentTests;

public sealed class CleanupDeliveryCommandTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "cleanup-result-outbox", Guid.NewGuid().ToString("N"));
    private readonly Guid rootId = Guid.NewGuid();
    private AgentJournal journal;
    private readonly ArchiveStaging staging;
    private StageCleanupAuthorization grant = null!;
    private string ArtifactDirectory => Path.Combine(home, "staging", grant.Plan.ArtifactId.ToString("N"));
    public CleanupDeliveryCommandTests()
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
    public async Task NoPendingDoesNotCallTransport()
    {
        var t=new Transport(); using var output=new StringWriter();
        Assert.Equal(0,await CleanupDeliveryCommand.RunAsync(journal,t,output));
        Assert.Equal(0,t.Calls); Assert.Equal("CleanupResultNoPending"+Environment.NewLine,output.ToString());
    }
    [Theory]
    [InlineData(400,3,"Pending")] [InlineData(409,3,"Pending")]
    [InlineData(401,2,"Pending")] [InlineData(403,2,"Pending")]
    [InlineData(404,3,"Pending")] [InlineData(429,3,"Pending")]
    [InlineData(500,3,"Pending")] [InlineData(503,3,"Pending")]
    public async Task StatusCodesAndOutputAreSanitized(int status,int code,string state)
    {
        await Prepare(); await Execute(); var r=Assert.Single(journal.PendingCleanupResults());
        var t=new Transport{Send=(_,_)=>throw new HttpRequestException("SECRET token arbitrary path",null,(System.Net.HttpStatusCode)status)};
        using var output=new StringWriter(); Assert.Equal(code,await CleanupDeliveryCommand.RunAsync(journal,t,output));
        Assert.Equal(state,journal.GetCleanupResultState(r.ResultId)); Assert.DoesNotContain("SECRET",output.ToString()); Assert.Equal(1,t.Calls);
    }
    [Fact]
    public async Task DeliveredExactlyOnePendingAndThenNoPending()
    {
        await Prepare();await Execute();var first=Assert.Single(journal.PendingCleanupResults());
        await Prepare();await Execute();var second=journal.PendingCleanupResults()[1];
        var t=new Transport();using var output=new StringWriter();
        Assert.Equal(0,await CleanupDeliveryCommand.RunAsync(journal,t,output));Assert.Equal(1,t.Calls);
        Assert.Equal(second,Assert.Single(journal.PendingCleanupResults()));Assert.Equal("Acked",journal.GetCleanupResultState(first.ResultId));
        Assert.Equal("CleanupResultDelivered"+Environment.NewLine,output.ToString());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DefinitiveRejectionIsBlockedButGenericDisabled409RemainsPending(bool invalid)
    {
        await Prepare();await Execute();var r=Assert.Single(journal.PendingCleanupResults());
        var t=new Transport{Send=(_,_)=>throw (invalid ? (HttpRequestException)new CleanupResultInvalidException() : new CleanupResultConflictException())};using var output=new StringWriter();
        Assert.Equal(4,await CleanupDeliveryCommand.RunAsync(journal,t,output));
        Assert.Equal("Blocked",journal.GetCleanupResultState(r.ResultId));
        Assert.Equal("CleanupResultBlocked"+Environment.NewLine,output.ToString());
    }
    [Fact]
    public async Task ReceiptMismatchBlocksWithoutSecretOutput()
    {
        await Prepare();await Execute();var r=Assert.Single(journal.PendingCleanupResults());
        var t=new Transport{Send=(r,_)=>Task.FromResult(Receipt(r) with{ArtifactId=Guid.NewGuid()})};using var output=new StringWriter();
        Assert.Equal(4,await CleanupDeliveryCommand.RunAsync(journal,t,output));Assert.Equal("Blocked",journal.GetCleanupResultState(r.ResultId));
        Assert.Equal("CleanupResultBlocked"+Environment.NewLine,output.ToString());
    }
    [Fact]
    public async Task CancellationIs130AndTimeoutIsTransient()
    {
        await Prepare();await Execute();var t=new Transport();using var output=new StringWriter();using var c=new CancellationTokenSource();c.Cancel();
        Assert.Equal(130,await CleanupDeliveryCommand.RunAsync(journal,t,output,c.Token));Assert.Equal(0,t.Calls);
        t.Send=(_,_)=>throw new TaskCanceledException("SECRET");Assert.Equal(3,await CleanupDeliveryCommand.RunAsync(journal,t,output));
        Assert.Single(journal.PendingCleanupResults());Assert.DoesNotContain("SECRET",output.ToString());
    }
    [Fact]
    public async Task StorageFailureIsConservativeAndSanitized()
    {
        journal.Dispose();using var output=new StringWriter();Assert.Equal(5,await CleanupDeliveryCommand.RunAsync(journal,new Transport(),output));
        Assert.Equal("CleanupResultStorageOrReplayFailure"+Environment.NewLine,output.ToString());
    }
    [Fact]
    public void ExactCommandGrammarHasNoWorkerFallback()
    {
        Assert.True(CleanupDeliveryCommand.SupportedArguments([]));Assert.True(CleanupDeliveryCommand.SupportedArguments(["import-token"]));
        Assert.True(CleanupDeliveryCommand.IsDeliveryCommand(["deliver-cleanup-result"]));
        foreach(var args in new[]{new[]{"deliver-cleanup-result","--path=SECRET"},new[]{"DELIVER-CLEANUP-RESULT"},new[]{"unknown"},new[]{"import-token","other"}})
        {Assert.False(CleanupDeliveryCommand.SupportedArguments(args));Assert.False(CleanupDeliveryCommand.IsDeliveryCommand(args));}
    }
    public void Dispose() { journal.Dispose(); if(Directory.Exists(home)) Directory.Delete(home,true); }
}
