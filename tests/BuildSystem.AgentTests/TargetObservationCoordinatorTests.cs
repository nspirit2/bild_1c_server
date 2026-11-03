using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetObservationCoordinatorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "target-observation-coordinator-tests", Guid.NewGuid().ToString("N"));
    private AgentJournal journal;
    private readonly TargetObservationAssignment assignment = NewAssignment();

    public TargetObservationCoordinatorTests() => journal = new AgentJournal(directory);

    private static TargetObservationAssignment NewAssignment() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 42, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private static ReportTargetObservation NewReport(TargetObservationAssignment assignment) => new(Guid.NewGuid(), assignment,
        Guid.NewGuid(), Guid.NewGuid(), TargetObservationOutcome.BothObservedAbsent,
        new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    private sealed class Transport : ITargetObservationTransport
    {
        public int Reads;
        public int Sends;
        public Func<Guid, CancellationToken, Task<TargetObservationAssignment>> Read { get; set; } =
            (_, _) => throw new InvalidOperationException("Unconfigured read");
        public Task<TargetObservationAssignment> ReadAsync(Guid requestId, CancellationToken ct)
        {
            Reads++;
            return Read(requestId, ct);
        }
        public Task<TargetObservationReceipt> ReportAsync(ReportTargetObservation report, CancellationToken ct)
        {
            Sends++;
            throw new InvalidOperationException("Coordinator must not deliver.");
        }
    }

    private sealed class Executor : ITargetObservationExecutor
    {
        public int Calls;
        public Func<TargetObservationAssignment, CancellationToken, Task<ReportTargetObservation>> Run { get; set; } =
            (_, _) => throw new InvalidOperationException("Unconfigured executor");
        public Task<ReportTargetObservation> ObserveAsync(TargetObservationAssignment assignment, CancellationToken ct)
        {
            Calls++;
            return Run(assignment, ct);
        }
    }

    private static void Redacted(TargetObservationCoordinatorException failure)
    {
        Assert.Equal("Target observation coordination failed.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    private (TargetObservationCoordinator Coordinator, Transport Transport, Executor Executor) Create(
        TargetObservationAssignment? current = null, ReportTargetObservation? result = null)
    {
        var server = current ?? assignment;
        var report = result ?? NewReport(server);
        var transport = new Transport { Read = (_, _) => Task.FromResult(server) };
        var executor = new Executor { Run = (_, _) => Task.FromResult(report) };
        return (new TargetObservationCoordinator(journal, transport, executor), transport, executor);
    }

    private void Reopen()
    {
        journal.Dispose();
        journal = new AgentJournal(directory);
    }

    [Fact]
    public async Task ReadsCurrentServerFirstPersistsAssignmentBeforeExecutorAndEnqueuesWithoutDelivery()
    {
        var (coordinator, transport, executor) = Create();
        var report = NewReport(assignment);
        transport.Read = (id, _) =>
        {
            Assert.Equal(assignment.ObservationRequestId, id);
            Assert.Null(journal.GetTargetObservationAssignment(id));
            return Task.FromResult(assignment);
        };
        executor.Run = (saved, _) =>
        {
            Assert.Equal(assignment, saved);
            Assert.Equal(assignment, journal.GetTargetObservationAssignment(saved.ObservationRequestId));
            Assert.Null(journal.GetTargetObservationReport(saved.ObservationRequestId));
            Assert.Empty(journal.PendingTargetObservations());
            Assert.False(saved.Executable);
            Assert.True(saved.ScheduledJobsMustRemainBlocked);
            return Task.FromResult(report);
        };
        Assert.Equal(report, await coordinator.ObserveAsync(assignment.ObservationRequestId));
        Assert.Equal(report, journal.GetTargetObservationReport(assignment.ObservationRequestId));
        Assert.Equal(report, Assert.Single(journal.PendingTargetObservations()));
        Assert.Equal(1, transport.Reads);
        Assert.Equal(1, executor.Calls);
        Assert.Equal(0, transport.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfterRestartReauthorizesAndReturnsSamePendingOrAcknowledgedHistory(bool ack)
    {
        var (coordinator, firstTransport, firstExecutor) = Create();
        var first = await coordinator.ObserveAsync(assignment.ObservationRequestId);
        if (ack) journal.AckTargetObservation(first.ReceiptId);
        Reopen();
        var (retry, transport, executor) = Create(result: NewReport(assignment));
        executor.Run = (_, _) => throw new InvalidOperationException("Must not observe again");
        Assert.Equal(first, await retry.ObserveAsync(assignment.ObservationRequestId));
        Assert.Equal(first, journal.GetTargetObservationReport(assignment.ObservationRequestId));
        Assert.Equal(ack ? 0 : 1, journal.PendingTargetObservations().Count);
        Assert.Equal(1, firstTransport.Reads);
        Assert.Equal(1, firstExecutor.Calls);
        Assert.Equal(1, transport.Reads);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(0, transport.Sends + firstTransport.Sends);
    }

    [Fact]
    public async Task HistoricalReportNeverBypassesServerReadFailureOrCancellation()
    {
        var (initial, _, _) = Create();
        var report = await initial.ObserveAsync(assignment.ObservationRequestId);
        var (retry, transport, executor) = Create();
        transport.Read = (_, _) => throw new HttpRequestException("private URL and token");
        Redacted(await Assert.ThrowsAsync<TargetObservationCoordinatorException>(() => retry.ObserveAsync(assignment.ObservationRequestId)));
        using var source = new CancellationTokenSource();
        transport.Read = (_, _) => { source.Cancel(); throw new InvalidOperationException("secret"); };
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.ObserveAsync(assignment.ObservationRequestId, source.Token));
        Assert.Equal(source.Token, cancelled.CancellationToken);
        Assert.Null(cancelled.InnerException);
        Assert.Equal(2, transport.Reads);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(report, journal.GetTargetObservationReport(assignment.ObservationRequestId));
    }

    [Theory]
    [InlineData("fence")]
    [InlineData("reservation")]
    [InlineData("mapping")]
    public async Task CurrentServerImmutablePinConflictRejectsEvenHistoricalReport(string pin)
    {
        var (initial, _, _) = Create();
        var report = await initial.ObserveAsync(assignment.ObservationRequestId);
        var changed = pin switch
        {
            "fence" => assignment with { FencingToken = assignment.FencingToken + 1 },
            "reservation" => assignment with { ReservationId = Guid.NewGuid() },
            _ => assignment with { SqlEndpointMappingId = Guid.NewGuid() }
        };
        var (retry, transport, executor) = Create(current: changed);
        Redacted(await Assert.ThrowsAsync<TargetObservationCoordinatorException>(() => retry.ObserveAsync(assignment.ObservationRequestId)));
        Assert.Equal(1, transport.Reads);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(report, journal.GetTargetObservationReport(assignment.ObservationRequestId));
    }

    [Theory]
    [InlineData("request")]
    [InlineData("agent")]
    [InlineData("fence")]
    [InlineData("stage")]
    [InlineData("mapping")]
    public async Task MalformedServerAssignmentsNeverPersistOrInvokeExecutor(string defect)
    {
        var malformed = defect switch
        {
            "request" => assignment with { ObservationRequestId = Guid.NewGuid() },
            "agent" => assignment with { AgentId = Guid.Empty },
            "fence" => assignment with { FencingToken = 0 },
            "stage" => assignment with { StageJobId = Guid.Empty },
            _ => assignment with { RacEndpointMappingId = Guid.Empty }
        };
        var (coordinator, transport, executor) = Create(current: malformed);
        Redacted(await Assert.ThrowsAsync<TargetObservationCoordinatorException>(() => coordinator.ObserveAsync(assignment.ObservationRequestId)));
        Assert.Equal(1, transport.Reads);
        Assert.Equal(0, executor.Calls);
        Assert.Null(journal.GetTargetObservationAssignment(assignment.ObservationRequestId));
    }

    [Theory]
    [InlineData("request")]
    [InlineData("intent")]
    [InlineData("agent")]
    [InlineData("operation")]
    [InlineData("environment")]
    [InlineData("stage")]
    [InlineData("fence")]
    [InlineData("reservation")]
    [InlineData("incarnation")]
    [InlineData("cluster")]
    [InlineData("server")]
    [InlineData("rac-mapping")]
    [InlineData("sql-mapping")]
    [InlineData("receipt")]
    [InlineData("rac")]
    [InlineData("sql")]
    [InlineData("pair")]
    [InlineData("outcome")]
    [InlineData("time")]
    public async Task UntrustedExecutorResultMustBeCompleteAndMatchPinnedAssignment(string defect)
    {
        var report = NewReport(assignment);
        var malformed = defect switch
        {
            "request" => report with { Assignment = assignment with { ObservationRequestId = Guid.NewGuid() } },
            "intent" => report with { Assignment = assignment with { RestoreIntentId = Guid.NewGuid() } },
            "agent" => report with { Assignment = assignment with { AgentId = Guid.NewGuid() } },
            "operation" => report with { Assignment = assignment with { OperationId = Guid.NewGuid() } },
            "environment" => report with { Assignment = assignment with { EnvironmentId = Guid.NewGuid() } },
            "stage" => report with { Assignment = assignment with { StageJobId = Guid.NewGuid() } },
            "fence" => report with { Assignment = assignment with { FencingToken = 43 } },
            "reservation" => report with { Assignment = assignment with { ReservationId = Guid.NewGuid() } },
            "incarnation" => report with { Assignment = assignment with { TargetIncarnationId = Guid.NewGuid() } },
            "cluster" => report with { Assignment = assignment with { CanonicalClusterId = Guid.NewGuid() } },
            "server" => report with { Assignment = assignment with { CanonicalSqlServerId = Guid.NewGuid() } },
            "rac-mapping" => report with { Assignment = assignment with { RacEndpointMappingId = Guid.NewGuid() } },
            "sql-mapping" => report with { Assignment = assignment with { SqlEndpointMappingId = Guid.NewGuid() } },
            "receipt" => report with { ReceiptId = Guid.Empty },
            "rac" => report with { RacObservationId = Guid.Empty },
            "sql" => report with { SqlObservationId = Guid.Empty },
            "pair" => report with { SqlObservationId = report.RacObservationId },
            "outcome" => report with { Outcome = (TargetObservationOutcome)42 },
            _ => report with { ObservedAt = default }
        };
        var (coordinator, transport, executor) = Create(result: malformed);
        Redacted(await Assert.ThrowsAsync<TargetObservationCoordinatorException>(() => coordinator.ObserveAsync(assignment.ObservationRequestId)));
        Assert.Equal(1, transport.Reads);
        Assert.Equal(1, executor.Calls);
        Assert.Equal(assignment, journal.GetTargetObservationAssignment(assignment.ObservationRequestId));
        Assert.Null(journal.GetTargetObservationReport(assignment.ObservationRequestId));
        Assert.Empty(journal.PendingTargetObservations());
    }

    [Fact]
    public async Task ExecutorFailureLeavesOnlyAssignmentAndExplicitRetryObservesFresh()
    {
        var (first, transport, executor) = Create();
        executor.Run = (_, _) => throw new InvalidOperationException("secret target details");
        Redacted(await Assert.ThrowsAsync<TargetObservationCoordinatorException>(() => first.ObserveAsync(assignment.ObservationRequestId)));
        Assert.Equal(assignment, journal.GetTargetObservationAssignment(assignment.ObservationRequestId));
        Assert.Null(journal.GetTargetObservationReport(assignment.ObservationRequestId));
        var (retry, secondTransport, secondExecutor) = Create();
        var report = await retry.ObserveAsync(assignment.ObservationRequestId);
        Assert.Equal(report, journal.GetTargetObservationReport(assignment.ObservationRequestId));
        Assert.Equal(1, transport.Reads);
        Assert.Equal(1, executor.Calls);
        Assert.Equal(1, secondTransport.Reads);
        Assert.Equal(1, secondExecutor.Calls);
    }

    [Fact]
    public async Task CancellationAfterPartialExecutorWorkLeavesNoReportAndAllowsFreshRetry()
    {
        using var source = new CancellationTokenSource();
        var (first, _, executor) = Create();
        executor.Run = (_, _) =>
        {
            source.Cancel();
            throw new InvalidOperationException("partial first target observation");
        };
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.ObserveAsync(assignment.ObservationRequestId, source.Token));
        Assert.Equal(source.Token, cancelled.CancellationToken);
        Assert.Null(cancelled.InnerException);
        Assert.Equal(assignment, journal.GetTargetObservationAssignment(assignment.ObservationRequestId));
        Assert.Null(journal.GetTargetObservationReport(assignment.ObservationRequestId));
        var (retry, _, freshExecutor) = Create();
        var fresh = await retry.ObserveAsync(assignment.ObservationRequestId);
        Assert.Equal(1, freshExecutor.Calls);
        Assert.Equal(fresh, Assert.Single(journal.PendingTargetObservations()));
    }

    [Fact]
    public async Task CancellationAfterExecutorReturnsDoesNotEnqueuePartialOrCompleteResult()
    {
        using var source = new CancellationTokenSource();
        var (coordinator, _, executor) = Create();
        executor.Run = (_, _) => { source.Cancel(); return Task.FromResult(NewReport(assignment)); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ObserveAsync(assignment.ObservationRequestId, source.Token));
        Assert.Equal(assignment, journal.GetTargetObservationAssignment(assignment.ObservationRequestId));
        Assert.Null(journal.GetTargetObservationReport(assignment.ObservationRequestId));
    }

    [Fact]
    public async Task EmptyRequestAndPrecancelledCallerNeverReadOrWrite()
    {
        var (coordinator, transport, executor) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.ObserveAsync(Guid.Empty));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.ObserveAsync(assignment.ObservationRequestId, source.Token));
        Assert.Equal(0, transport.Reads);
        Assert.Equal(0, executor.Calls);
        Assert.Null(journal.GetTargetObservationAssignment(assignment.ObservationRequestId));
    }

    [Fact]
    public async Task ReentrantCallRejectsImmediatelyWithoutSecondRead()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TargetObservationAssignment>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (coordinator, transport, executor) = Create();
        transport.Read = (_, _) => { entered.TrySetResult(); return release.Task; };
        var ongoing = coordinator.ObserveAsync(assignment.ObservationRequestId);
        await entered.Task;
        Redacted(await Assert.ThrowsAsync<TargetObservationCoordinatorException>(() => coordinator.ObserveAsync(assignment.ObservationRequestId)));
        Assert.Equal(1, transport.Reads);
        Assert.Equal(0, executor.Calls);
        release.SetResult(assignment);
        await ongoing;
        Assert.Equal(1, executor.Calls);
    }

    public void Dispose()
    {
        journal.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}