using System.Net;
using System.Net.Http.Json;
using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class StageCompletionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "stage-completion-tests", Guid.NewGuid().ToString("N"));
    private AgentJournal journal;
    private readonly ReportStagedArtifact artifact;
    private readonly CompleteStage completion;
    public StageCompletionTests()
    {
        journal = new(directory);
        artifact = new(Guid.NewGuid(), 7, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), 10, "dt");
        completion = new(artifact.JobId, artifact.FencingToken, artifact.ArtifactId);
        journal.StoreGrant(new(artifact.JobId, artifact.FencingToken, DateTimeOffset.UtcNow.AddMinutes(2), "ArchiveStageOnly", "{}"));
        journal.EnqueueArtifact(artifact);
    }
    private void AckArtifact() => journal.AcknowledgeArtifact(artifact.ArtifactId);
    private void Reopen() { journal.Dispose(); journal = new(directory); }

    [Fact]
    public async Task LostResponseReopenAndExpiredReplaySettlesOnlyMatchingCompletion()
    {
        AckArtifact(); journal.EnqueueCompletion(completion);
        var sent = new List<CompleteStage>();
        var fake = new Fake((report, _) => { sent.Add(report); throw new HttpRequestException("lost response"); });
        Assert.Equal(StageCompletionDeliveryResult.RetryLater, await new StageCompletionDelivery(journal, fake).FlushAsync(default));
        Assert.False(journal.IsSettled(artifact.JobId, artifact.FencingToken));
        journal.UpdateExpiry(artifact.JobId, artifact.FencingToken, DateTimeOffset.UtcNow.AddSeconds(-1));
        Reopen(); journal.EnqueueCompletion(completion);
        fake.Handler = (report, _) => { sent.Add(report); return Task.FromResult(Receipt(report)); };
        Assert.Equal(StageCompletionDeliveryResult.Acknowledged, await new StageCompletionDelivery(journal, fake).FlushAsync(default));
        Assert.Equal(new[] { completion, completion }, sent);
        Assert.True(journal.IsSettled(completion.JobId, completion.FencingToken));
        Assert.Equal("Acked", journal.ArtifactState(artifact.ArtifactId));
        Assert.Equal("Acked", journal.CompletionState(completion.JobId, completion.FencingToken));
        Assert.Empty(journal.Pending());
        Reopen(); journal.EnqueueCompletion(completion);
        Assert.Equal(StageCompletionDeliveryResult.Empty, await new StageCompletionDelivery(journal, fake).FlushAsync(default));
    }

    [Fact]
    public void EnqueueRequiresAckedArtifactAndPreservesImmutableIdentity()
    {
        Assert.Throws<InvalidOperationException>(() => journal.EnqueueCompletion(completion));
        AckArtifact(); journal.EnqueueCompletion(completion);
        Assert.Throws<InvalidOperationException>(() => journal.EnqueueCompletion(completion with { ArtifactId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => journal.AcknowledgeCompletion(Receipt(completion) with { ArtifactId = Guid.NewGuid() }));
        Assert.False(journal.IsSettled(completion.JobId, completion.FencingToken));
    }

    [Fact]
    public async Task NewlyExpiredCompletionBlocksWithoutHttpAndCannotClear()
    {
        AckArtifact(); journal.UpdateExpiry(completion.JobId, completion.FencingToken, DateTimeOffset.UtcNow.AddSeconds(-1));
        journal.EnqueueCompletion(completion); Reopen(); journal.EnqueueCompletion(completion);
        var fake = new Fake();
        Assert.Equal(StageCompletionDeliveryResult.Blocked, await new StageCompletionDelivery(journal, fake).FlushAsync(default));
        Assert.Equal("LeaseExpired", Assert.Single(journal.PendingCompletions()).FailureCode);
        Assert.Equal(0, fake.Calls);
        Assert.False(journal.IsSettled(completion.JobId, completion.FencingToken));
    }

    [Theory]
    [InlineData(401, "AuthRejected")]
    [InlineData(403, "AuthRejected")]
    [InlineData(409, "LeaseRejected")]
    [InlineData(410, "LeaseRejected")]
    public async Task PermanentResponseRemainsBlockedAcrossRestart(int status, string failure)
    {
        AckArtifact(); journal.EnqueueCompletion(completion);
        var fake = new Fake((_, _) => throw new HttpRequestException("not stored", null, (HttpStatusCode)status));
        Assert.Equal(StageCompletionDeliveryResult.Blocked, await new StageCompletionDelivery(journal, fake).FlushAsync(default));
        Reopen(); journal.EnqueueCompletion(completion);
        Assert.Equal(StageCompletionDeliveryResult.Blocked, await new StageCompletionDelivery(journal, fake).FlushAsync(default));
        Assert.Equal(1, fake.Calls); Assert.Equal(failure, Assert.Single(journal.PendingCompletions()).FailureCode);
        Assert.False(journal.IsSettled(completion.JobId, completion.FencingToken));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task TransientResponseLeavesCompletionPending(int status)
    {
        AckArtifact(); journal.EnqueueCompletion(completion);
        var fake = new Fake((_, _) => throw new HttpRequestException("not stored", null, (HttpStatusCode)status));
        Assert.Equal(StageCompletionDeliveryResult.RetryLater, await new StageCompletionDelivery(journal, fake).FlushAsync(default));
        Assert.Equal("Pending", journal.CompletionState(completion.JobId, completion.FencingToken));
        Assert.False(journal.IsSettled(completion.JobId, completion.FencingToken));
    }

    [Theory]
    [InlineData("job")]
    [InlineData("fence")]
    [InlineData("artifact")]
    [InlineData("state")]
    public async Task MismatchCannotSettleLease(string field)
    {
        AckArtifact(); journal.EnqueueCompletion(completion);
        var receipt = Receipt(completion);
        receipt = field switch { "job" => receipt with { JobId = Guid.NewGuid() }, "fence" => receipt with { FencingToken = 99 },
            "artifact" => receipt with { ArtifactId = Guid.NewGuid() }, _ => receipt with { State = "Ready" } };
        var fake = new Fake((_, _) => Task.FromResult(receipt));
        Assert.Equal(StageCompletionDeliveryResult.Blocked, await new StageCompletionDelivery(journal, fake).FlushAsync(default));
        Assert.False(journal.IsSettled(completion.JobId, completion.FencingToken));
        Assert.Equal("ReceiptMismatch", Assert.Single(journal.PendingCompletions()).FailureCode);
    }

    [Fact]
    public async Task CallerCancellationEvenAfterResponseCannotAcknowledge()
    {
        AckArtifact(); journal.EnqueueCompletion(completion);
        using var cancellation = new CancellationTokenSource();
        var fake = new Fake((r, _) => { cancellation.Cancel(); return Task.FromResult(Receipt(r)); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StageCompletionDelivery(journal, fake).FlushAsync(cancellation.Token));
        Assert.Equal("Pending", journal.CompletionState(completion.JobId, completion.FencingToken));
        Assert.False(journal.IsSettled(completion.JobId, completion.FencingToken));
    }

    [Fact]
    public async Task OperatorSettlementBlocksPendingCompletionWithoutAcknowledgingIt()
    {
        AckArtifact(); journal.EnqueueCompletion(completion); journal.MarkSettled(completion.JobId, completion.FencingToken);
        Assert.Equal(StageCompletionDeliveryResult.Blocked, await new StageCompletionDelivery(journal, new Fake()).FlushAsync(default));
        Assert.Equal("Blocked", journal.CompletionState(completion.JobId, completion.FencingToken));
    }

    [Fact]
    public async Task TransportUsesDedicatedRouteAndMatchingBoundedReceipt()
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal("/api/agent/stage/complete", request.RequestUri!.AbsolutePath);
            Assert.Equal(new string('A', 64), request.Headers.Authorization!.Parameter);
            Assert.Equal(completion, await request.Content!.ReadFromJsonAsync<CompleteStage>());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Receipt(completion)) };
        });
        using var transport = new AgentTransport(new Uri("https://example.test"), new string('A', 64), handler);
        Assert.Equal(Receipt(completion), await transport.CompleteStageAsync(completion, default));
    }

    private static StageCompletionReceipt Receipt(CompleteStage r) => new(r.JobId, r.FencingToken, r.ArtifactId, "StageCompleted");
    private sealed class Fake(Func<CompleteStage, CancellationToken, Task<StageCompletionReceipt>>? handler = null) : IStageCompletionTransport
    {
        public int Calls;
        public Func<CompleteStage, CancellationToken, Task<StageCompletionReceipt>> Handler = handler ?? ((r, _) => Task.FromResult(Receipt(r)));
        public Task<StageCompletionReceipt> CompleteStageAsync(CompleteStage report, CancellationToken ct) { Calls++; return Handler(report, ct); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handler(request);
    }
    public void Dispose() { journal.Dispose(); Directory.Delete(directory, true); }
}
// end
