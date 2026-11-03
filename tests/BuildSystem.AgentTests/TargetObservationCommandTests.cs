using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetObservationCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "observation-command-" + Guid.NewGuid().ToString("N"));
    private sealed class Transport(TargetObservationAssignment assignment) : ITargetObservationTransport
    {
        public int Reads, Sends;
        public bool FailRead, FailSend;
        public Task<TargetObservationAssignment> ReadAsync(Guid id, CancellationToken ct)
        { Reads++; if (FailRead) throw new IOException("private"); return Task.FromResult(assignment); }
        public Task<TargetObservationReceipt> ReportAsync(ReportTargetObservation report, CancellationToken ct)
        { Sends++; if (FailSend) throw new IOException("private"); return Task.FromResult(new TargetObservationReceipt(report.ReceiptId, report.Assignment.ObservationRequestId, "Recorded")); }
    }
    private sealed class Executor : ITargetObservationExecutor
    {
        public int Calls;
        public Task<ReportTargetObservation> ObserveAsync(TargetObservationAssignment assignment, CancellationToken ct)
        { Calls++; return Task.FromResult(new ReportTargetObservation(Guid.NewGuid(), assignment, Guid.NewGuid(), Guid.NewGuid(), TargetObservationOutcome.BothObservedAbsent, DateTimeOffset.UtcNow)); }
    }
    private static TargetObservationAssignment Assignment() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void ArgumentsAreExactAndDoNotAcceptFlagsOrAlternateGuidFormats()
    {
        var id = Guid.NewGuid();
        Assert.True(TargetObservationCommand.TryParse(new[] { "observe-target", id.ToString("D") }, out var parsed));
        Assert.Equal(id, parsed);
        foreach (var args in new[] { Array.Empty<string>(), new[] { "observe-target" }, new[] { "observe-target", id.ToString("N") }, new[] { "observe-target", Guid.Empty.ToString() }, new[] { "observe-target", id.ToString(), "--force" }, new[] { "restore", id.ToString() } })
            Assert.False(TargetObservationCommand.TryParse(args, out _));
    }

    [Fact]
    public async Task ExplicitCommandRecordsOnceAndReturnsHistoricalAckWithoutReobserving()
    {
        using var journal = new AgentJournal(directory); var assignment = Assignment();
        var transport = new Transport(assignment); var executor = new Executor();
        var coordinator = new TargetObservationCoordinator(journal, transport, executor);
        var delivery = new TargetObservationOutboxDelivery(journal, transport); using var output = new StringWriter();
        Assert.Equal(0, await TargetObservationCommand.RunAsync(assignment.ObservationRequestId, coordinator, delivery, output));
        Assert.Equal(0, await TargetObservationCommand.RunAsync(assignment.ObservationRequestId, coordinator, delivery, output));
        Assert.Equal(2, transport.Reads); Assert.Equal(1, transport.Sends); Assert.Equal(1, executor.Calls);
        Assert.Empty(journal.PendingTargetObservations());
        Assert.Equal("TargetObservationRecorded" + Environment.NewLine + "TargetObservationHistoricalRecorded" + Environment.NewLine, output.ToString());
    }

    [Theory]
    [InlineData(true, false, 4, "TargetObservationBlocked")]
    [InlineData(false, true, 5, "TargetObservationDeliveryOrStorageFailure")]
    public async Task FailuresAreRedactedAndDeliveryFailurePreservesPending(bool read, bool send, int expected, string status)
    {
        using var journal = new AgentJournal(directory); var assignment = Assignment();
        var transport = new Transport(assignment) { FailRead = read, FailSend = send }; var executor = new Executor();
        using var output = new StringWriter();
        Assert.Equal(expected, await TargetObservationCommand.RunAsync(assignment.ObservationRequestId,
            new(journal, transport, executor), new(journal, transport), output));
        Assert.Equal(status + Environment.NewLine, output.ToString());
        Assert.Equal(send ? 1 : 0, journal.PendingTargetObservations().Count);
    }

    [Fact]
    public async Task InvalidOrPrecancelledRequestDoesNotPerformIo()
    {
        using var journal = new AgentJournal(directory); var assignment = Assignment(); var transport = new Transport(assignment); var executor = new Executor();
        var coordinator = new TargetObservationCoordinator(journal, transport, executor); var delivery = new TargetObservationOutboxDelivery(journal, transport);
        using var output = new StringWriter();
        Assert.Equal(64, await TargetObservationCommand.RunAsync(Guid.Empty, coordinator, delivery, output));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Equal(130, await TargetObservationCommand.RunAsync(assignment.ObservationRequestId, coordinator, delivery, output, cancel.Token));
        Assert.Equal(0, transport.Reads); Assert.Equal(0, transport.Sends); Assert.Equal(0, executor.Calls);
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        if (!Path.GetFileName(resolved).StartsWith("observation-command-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}

// End.
