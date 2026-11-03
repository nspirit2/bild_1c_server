using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetObservationOutboxDeliveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "target-observation-delivery-tests", Guid.NewGuid().ToString("N"));
    private AgentJournal journal;

    public TargetObservationOutboxDeliveryTests() => journal = new AgentJournal(directory);

    private ReportTargetObservation Enqueue(int index = 0)
    {
        var assignment = new TargetObservationAssignment(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 42, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var report = new ReportTargetObservation(Guid.NewGuid(), assignment, Guid.NewGuid(), Guid.NewGuid(),
            (TargetObservationOutcome)(index % 4), new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(index + 123));
        journal.SaveTargetObservationAssignment(assignment);
        journal.EnqueueTargetObservation(report);
        return report;
    }

    private void Reopen()
    {
        journal.Dispose();
        journal = new AgentJournal(directory);
    }

    private static TargetObservationReceipt Ack(ReportTargetObservation report) =>
        new(report.ReceiptId, report.Assignment.ObservationRequestId, "Recorded");

    private sealed class Transport : ITargetObservationTransport
    {
        public List<ReportTargetObservation> Reports { get; } = [];
        public int Reads { get; private set; }
        public Func<ReportTargetObservation, CancellationToken, Task<TargetObservationReceipt>> Send { get; set; } =
            (report, _) => Task.FromResult(Ack(report));
        public Task<TargetObservationAssignment> ReadAsync(Guid id, CancellationToken ct)
        {
            Reads++;
            throw new InvalidOperationException("Delivery must never read an assignment.");
        }
        public Task<TargetObservationReceipt> ReportAsync(ReportTargetObservation report, CancellationToken ct)
        {
            Reports.Add(report);
            return Send(report, ct);
        }
    }

    private static void AssertRedacted(TargetObservationOutboxException failure)
    {
        Assert.Equal("Target observation outbox delivery failed.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task AddressedDeliverySkipsOlderReportsAndRetainsAcknowledgedHistory()
    {
        var older = Enqueue(); var selected = Enqueue(1); var newer = Enqueue(2);
        var transport = new Transport(); var delivery = new TargetObservationOutboxDelivery(journal, transport);
        Assert.Equal(1, await delivery.DeliverRequestAsync(selected.Assignment.ObservationRequestId));
        Assert.Equal(selected, Assert.Single(transport.Reports));
        Assert.Equal(new[] { older, newer }, journal.PendingTargetObservations());
        Reopen();
        Assert.Equal(selected, journal.GetTargetObservationReport(selected.Assignment.ObservationRequestId));
        Assert.Null(journal.GetPendingTargetObservationReport(selected.Assignment.ObservationRequestId));
        Assert.Equal(0, await new TargetObservationOutboxDelivery(journal, transport).DeliverRequestAsync(selected.Assignment.ObservationRequestId));
        Assert.Single(transport.Reports);
    }

    [Fact]
    public async Task AddressedDeliveryRejectsUnknownOrEmptyRequestWithoutSendingOtherReports()
    {
        var pending = Enqueue(); var transport = new Transport(); var delivery = new TargetObservationOutboxDelivery(journal, transport);
        await Assert.ThrowsAsync<ArgumentException>(() => delivery.DeliverRequestAsync(Guid.Empty));
        AssertRedacted(await Assert.ThrowsAsync<TargetObservationOutboxException>(() => delivery.DeliverRequestAsync(Guid.NewGuid())));
        Assert.Equal(pending, Assert.Single(journal.PendingTargetObservations()));
        Assert.Empty(transport.Reports);
    }

    [Fact]
    public async Task AddressedLostAckRetainsExactSelectedPayloadForRetry()
    {
        var older = Enqueue(); var selected = Enqueue(1);
        var transport = new Transport { Send = (_, _) => throw new IOException("private") };
        await Assert.ThrowsAsync<TargetObservationOutboxException>(() =>
            new TargetObservationOutboxDelivery(journal, transport).DeliverRequestAsync(selected.Assignment.ObservationRequestId));
        Reopen(); transport.Send = (report, _) => Task.FromResult(Ack(report));
        Assert.Equal(1, await new TargetObservationOutboxDelivery(journal, transport).DeliverRequestAsync(selected.Assignment.ObservationRequestId));
        Assert.Equal(new[] { selected, selected }, transport.Reports);
        Assert.Equal(older, Assert.Single(journal.PendingTargetObservations()));
    }

    [Fact]
    public async Task EmptySnapshotDoesNotReadOrReport()
    {
        var transport = new Transport();
        Assert.Equal(0, await new TargetObservationOutboxDelivery(journal, transport).DeliverAsync());
        Assert.Empty(transport.Reports);
        Assert.Equal(0, transport.Reads);
    }

    [Fact]
    public async Task BoundedSequentialDeliveryPreservesEveryHistoricalPayloadAndInsertionOrder()
    {
        var reports = Enumerable.Range(0, 5).Select(Enqueue).ToArray();
        var transport = new Transport();
        transport.Send = (report, _) =>
        {
            var index = transport.Reports.Count - 1;
            Assert.Equal(reports[index], report);
            // Earlier exact responses are durably acknowledged before the next send.
            Assert.Equal(reports.Skip(index), journal.PendingTargetObservations());
            Assert.False(report.Executable);
            Assert.True(report.ScheduledJobsMustRemainBlocked);
            return Task.FromResult(Ack(report));
        };
        var delivery = new TargetObservationOutboxDelivery(journal, transport);
        Assert.Equal(4, await delivery.DeliverAsync(4));
        Assert.Equal(reports.Take(4), transport.Reports);
        Assert.Equal(reports[4], Assert.Single(journal.PendingTargetObservations()));
        Assert.Equal(0, transport.Reads);
    }

    [Fact]
    public async Task DefaultLimitIsOneHundredAndMaximumLimitIsAccepted()
    {
        var reports = Enumerable.Range(0, 102).Select(Enqueue).ToArray();
        var transport = new Transport();
        var delivery = new TargetObservationOutboxDelivery(journal, transport);
        Assert.Equal(100, await delivery.DeliverAsync());
        Assert.Equal(reports.Take(100), transport.Reports);
        Assert.Equal(reports.Skip(100), journal.PendingTargetObservations());
        Assert.Equal(2, await delivery.DeliverAsync(1000));
        Assert.Empty(journal.PendingTargetObservations());
    }

    [Fact]
    public async Task SnapshotDoesNotDrainReportsEnqueuedDuringDelivery()
    {
        var original = Enqueue();
        ReportTargetObservation? appended = null;
        var transport = new Transport { Send = (report, _) =>
        {
            appended = Enqueue(1);
            return Task.FromResult(Ack(report));
        } };
        Assert.Equal(1, await new TargetObservationOutboxDelivery(journal, transport).DeliverAsync());
        Assert.Equal(original, Assert.Single(transport.Reports));
        Assert.Equal(appended, Assert.Single(journal.PendingTargetObservations()));
    }

    [Fact]
    public async Task LostAcknowledgementAcrossRecreationReplaysIdenticalDurableReport()
    {
        var report = Enqueue();
        var accepted = new List<ReportTargetObservation>();
        var lostAck = new Transport { Send = (r, _) =>
        {
            accepted.Add(r); // Server stored this historical report, but its response was lost.
            throw new HttpRequestException("raw endpoint and secret");
        } };
        AssertRedacted(await Assert.ThrowsAsync<TargetObservationOutboxException>(() =>
            new TargetObservationOutboxDelivery(journal, lostAck).DeliverAsync()));
        Reopen();
        Assert.Equal(report, Assert.Single(journal.PendingTargetObservations()));
        var retry = new Transport();
        Assert.Equal(1, await new TargetObservationOutboxDelivery(journal, retry).DeliverAsync());
        Assert.Equal(Assert.Single(accepted), Assert.Single(retry.Reports));
        Assert.Equal(report, Assert.Single(retry.Reports));
        Assert.Equal(0, lostAck.Reads + retry.Reads);
    }

    [Fact]
    public async Task ExactAcknowledgementRetainsHistoryAndCannotResendOrReplaceAfterRestart()
    {
        var report = Enqueue();
        var transport = new Transport();
        Assert.Equal(1, await new TargetObservationOutboxDelivery(journal, transport).DeliverAsync());
        Reopen();
        Assert.Equal(report.Assignment, journal.GetTargetObservationAssignment(report.Assignment.ObservationRequestId));
        journal.EnqueueTargetObservation(report);
        journal.AckTargetObservation(report.ReceiptId);
        Assert.Throws<InvalidOperationException>(() => journal.EnqueueTargetObservation(report with { ReceiptId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => journal.EnqueueTargetObservation(report with { ObservedAt = report.ObservedAt.AddTicks(1) }));
        Assert.Equal(0, await new TargetObservationOutboxDelivery(journal, transport).DeliverAsync());
        Assert.Single(transport.Reports);
    }

    [Fact]
    public async Task FailureStopsChainButEarlierExactAcknowledgementsRemainDurable()
    {
        var reports = Enumerable.Range(0, 3).Select(Enqueue).ToArray();
        var transport = new Transport { Send = (report, _) => report == reports[1]
            ? throw new InvalidOperationException("secret historical payload") : Task.FromResult(Ack(report)) };
        AssertRedacted(await Assert.ThrowsAsync<TargetObservationOutboxException>(() =>
            new TargetObservationOutboxDelivery(journal, transport).DeliverAsync()));
        Assert.Equal(reports.Take(2), transport.Reports);
        Reopen();
        Assert.Equal(reports.Skip(1), journal.PendingTargetObservations());
        Assert.Equal(2, await new TargetObservationOutboxDelivery(journal, new Transport()).DeliverAsync());
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("request")]
    [InlineData("state")]
    [InlineData("case")]
    [InlineData("null-state")]
    [InlineData("null")]
    public async Task EveryAcknowledgementIdentityAndStatePinMustMatchOrStopChain(string mismatch)
    {
        var reports = Enumerable.Range(0, 2).Select(Enqueue).ToArray();
        var transport = new Transport { Send = (report, _) =>
        {
            var receipt = Ack(report);
            receipt = mismatch switch
            {
                "receipt" => receipt with { ReceiptId = Guid.NewGuid() },
                "request" => receipt with { ObservationRequestId = Guid.NewGuid() },
                "state" => receipt with { State = "Accepted" },
                "case" => receipt with { State = "recorded" },
                "null-state" => receipt with { State = null! },
                _ => null!
            };
            if (receipt is not null)
            {
                Assert.False(receipt.Executable);
                Assert.True(receipt.ScheduledJobsMustRemainBlocked);
            }
            return Task.FromResult<TargetObservationReceipt>(receipt!);
        } };
        AssertRedacted(await Assert.ThrowsAsync<TargetObservationOutboxException>(() =>
            new TargetObservationOutboxDelivery(journal, transport).DeliverAsync()));
        Assert.Equal(reports[0], Assert.Single(transport.Reports));
        Assert.Equal(reports, journal.PendingTargetObservations());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1001)]
    [InlineData(int.MaxValue)]
    public async Task InvalidLimitsFailEvenForEmptyOutboxBeforeTransport(int limit)
    {
        var transport = new Transport();
        var delivery = new TargetObservationOutboxDelivery(journal, transport);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => delivery.DeliverAsync(limit));
        Enqueue();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => delivery.DeliverAsync(limit));
        Assert.Empty(transport.Reports);
        Assert.Equal(0, transport.Reads);
        Assert.Single(journal.PendingTargetObservations());
    }

    [Fact]
    public async Task CallerCancellationBeforeDeliveryDoesNotSend()
    {
        var report = Enqueue();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new Transport();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TargetObservationOutboxDelivery(journal, transport).DeliverAsync(ct: cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(transport.Reports);
        Assert.Equal(report, Assert.Single(journal.PendingTargetObservations()));
    }

    [Fact]
    public async Task CallerCancellationDuringTransportRemainsCancellationWithoutLeakingRawData()
    {
        var report = Enqueue();
        using var cancellation = new CancellationTokenSource();
        var transport = new Transport { Send = (_, ct) =>
        {
            Assert.Equal(cancellation.Token, ct);
            cancellation.Cancel();
            throw new OperationCanceledException("raw transport secret", ct);
        } };
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TargetObservationOutboxDelivery(journal, transport).DeliverAsync(ct: cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.DoesNotContain("raw transport secret", exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.Equal(report, Assert.Single(journal.PendingTargetObservations()));
    }

    [Fact]
    public async Task CancellationAfterResponseBeforeJournalAckLeavesExactReportPending()
    {
        var reports = Enumerable.Range(0, 2).Select(Enqueue).ToArray();
        using var cancellation = new CancellationTokenSource();
        var transport = new Transport { Send = (report, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Ack(report));
        } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TargetObservationOutboxDelivery(journal, transport).DeliverAsync(ct: cancellation.Token));
        Assert.Equal(reports[0], Assert.Single(transport.Reports));
        Reopen();
        Assert.Equal(reports, journal.PendingTargetObservations());
    }

    [Fact]
    public async Task TransportCancellationWithoutCallerCancellationIsRedactedFailureAndCanRetry()
    {
        var report = Enqueue();
        var transport = new Transport { Send = (_, _) => throw new TaskCanceledException("raw timeout secret") };
        var delivery = new TargetObservationOutboxDelivery(journal, transport);
        AssertRedacted(await Assert.ThrowsAsync<TargetObservationOutboxException>(() => delivery.DeliverAsync()));
        Assert.Equal(report, Assert.Single(journal.PendingTargetObservations()));
        transport.Send = (r, _) => Task.FromResult(Ack(r));
        Assert.Equal(1, await delivery.DeliverAsync());
        Assert.Equal(new[] { report, report }, transport.Reports);
    }

    [Fact]
    public async Task ConcurrentCallIsRejectedRatherThanQueuedAndDoesNotDuplicateSend()
    {
        var report = Enqueue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<TargetObservationReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport { Send = (_, _) => { entered.SetResult(); return response.Task; } };
        var delivery = new TargetObservationOutboxDelivery(journal, transport);
        var first = delivery.DeliverAsync();
        await entered.Task;
        try
        {
            AssertRedacted(await Assert.ThrowsAsync<TargetObservationOutboxException>(() => delivery.DeliverAsync()));
            Assert.Equal(report, Assert.Single(transport.Reports));
            Assert.Equal(report, Assert.Single(journal.PendingTargetObservations()));
        }
        finally { response.TrySetResult(Ack(report)); }
        Assert.Equal(1, await first);
        Assert.Equal(0, await delivery.DeliverAsync());
        Assert.Single(transport.Reports);
    }

    [Fact]
    public async Task ReentrantCallIsRejectedWithoutBlockingOuterDelivery()
    {
        var report = Enqueue();
        var transport = new Transport();
        var delivery = new TargetObservationOutboxDelivery(journal, transport);
        transport.Send = async (r, _) =>
        {
            AssertRedacted(await Assert.ThrowsAsync<TargetObservationOutboxException>(() => delivery.DeliverAsync()));
            return Ack(r);
        };
        Assert.Equal(1, await delivery.DeliverAsync());
        Assert.Equal(report, Assert.Single(transport.Reports));
        Assert.Empty(journal.PendingTargetObservations());
    }

    public void Dispose()
    {
        journal.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}