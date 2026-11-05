using BuildSystem.Domain;
using Xunit;

namespace BuildSystem.UnitTests;

public sealed class TargetCreationOperationTests
{
    private static Guid[] Ids() => Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
    private static TargetCreationOperationPins Pins(Guid[]? ids = null)
    {
        ids ??= Ids();
        return new(ids[0], ids[1], ids[2], ids[3], ids[4], ids[5], ids[6], ids[7], ids[8], ids[9]);
    }

    private static void Error(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<DomainRuleException>(action).Code);

    private static void Blocked(TargetCreationOperation operation)
    {
        Assert.False(operation.Executable);
        Assert.True(operation.ScheduledJobsMustRemainBlocked);
    }

    [Fact]
    public void RegistrationCopiesAllPinsAndDerivesExactNamesWithoutObservationOrAuthority()
    {
        var ids = Ids();
        var pins = Pins(ids);
        var operation = TargetCreationOperation.Register(pins);
        Assert.NotSame(pins, operation.Pins);
        Assert.Equal(pins, operation.Pins);
        Assert.Equal(ids[0], operation.Pins.OperationId);
        Assert.Equal(ids[1], operation.Pins.RestoreIntentId);
        Assert.Equal(ids[2], operation.Pins.EnvironmentId);
        Assert.Equal(ids[3], operation.Pins.StageJobId);
        Assert.Equal(ids[4], operation.Pins.ReservationId);
        Assert.Equal(ids[5], operation.Pins.ArtifactId);
        Assert.Equal(ids[6], operation.Pins.AgentId);
        Assert.Equal(ids[7], operation.Pins.TargetIncarnationId);
        Assert.Equal(ids[8], operation.Pins.CanonicalClusterId);
        Assert.Equal(ids[9], operation.Pins.CanonicalSqlServerId);
        var name = "bs_test_" + ids[4].ToString("N");
        Assert.Equal(name, operation.Pins.InfobaseName);
        Assert.Equal(name, operation.Pins.SqlDatabaseName);
        Assert.Equal(40, name.Length);
        Assert.Equal(TargetCreationOperationState.Registered, operation.State);
        Assert.Equal(1L, operation.Revision);
        Assert.Null(operation.AttemptId);
        Assert.Null(operation.AttemptExpectedRevision);
        Assert.Null(operation.QuarantineReason);
        Blocked(operation);
        ids[4] = Guid.NewGuid();
        Assert.Equal(name, operation.Pins.SqlDatabaseName);
    }

    [Fact]
    public void RegisterRejectsNullPinsWithFixedDomainError()
    {
        Error("InvalidPins", () => TargetCreationOperation.Register(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void EveryPinRejectsEmptyGuid(int index)
    {
        var ids = Ids();
        ids[index] = Guid.Empty;
        Error("InvalidId", () => Pins(ids));
    }

    [Fact]
    public void BeginRecordsOnlyMetadataAndDoesNotMutateOriginal()
    {
        var original = TargetCreationOperation.Register(Pins());
        var attempt = Guid.NewGuid();
        var receipt = original.BeginAttempt(1, attempt);
        var recorded = receipt.NewState;
        Assert.True(receipt.NewlyRecorded);
        Assert.NotSame(original, recorded);
        Assert.Equal(TargetCreationOperationState.AttemptRecorded, recorded.State);
        Assert.Equal(2L, recorded.Revision);
        Assert.Equal(attempt, recorded.AttemptId);
        Assert.Equal(1L, recorded.AttemptExpectedRevision);
        Assert.Null(recorded.QuarantineReason);
        Assert.Equal(original.Pins, recorded.Pins);
        Assert.NotSame(original.Pins, recorded.Pins);
        Assert.Equal(TargetCreationOperationState.Registered, original.State);
        Assert.Equal(1L, original.Revision);
        Assert.Null(original.AttemptId);
        Blocked(original);
        Blocked(recorded);
    }

    [Fact]
    public void OnlyExactOriginalRevisionAndAttemptReplayWhileAttemptRecorded()
    {
        var attempt = Guid.NewGuid();
        var recorded = TargetCreationOperation.Register(Pins()).BeginAttempt(1, attempt).NewState;
        var replay = recorded.BeginAttempt(1, attempt);
        Assert.False(replay.NewlyRecorded);
        Assert.Same(recorded, replay.NewState);
        Assert.Equal(2L, replay.NewState.Revision);
        Blocked(replay.NewState);
        Error("RevisionConflict", () => recorded.BeginAttempt(1, Guid.NewGuid()));
        Error("InvalidTransition", () => recorded.BeginAttempt(2, attempt));
        Error("InvalidTransition", () => recorded.BeginAttempt(2, Guid.NewGuid()));
        Error("RevisionConflict", () => recorded.BeginAttempt(3, attempt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void NonPositiveRevisionsFailOnEveryState(long revision)
    {
        var registered = TargetCreationOperation.Register(Pins());
        var recorded = registered.BeginAttempt(1, Guid.NewGuid()).NewState;
        var quarantined = recorded.Quarantine(2, TargetCreationQuarantineReason.OutcomeUnknown);
        foreach (var operation in new[] { registered, recorded, quarantined })
        {
            Error("InvalidRevision", () => operation.BeginAttempt(revision, Guid.NewGuid()));
            Error("InvalidRevision", () => operation.Quarantine(revision, TargetCreationQuarantineReason.Cancelled));
        }
    }

    [Fact]
    public void InvalidAttemptAndRegisteredRevisionMismatchesAreRejected()
    {
        var registered = TargetCreationOperation.Register(Pins());
        Error("InvalidId", () => registered.BeginAttempt(1, Guid.Empty));
        Error("RevisionConflict", () => registered.BeginAttempt(2, Guid.NewGuid()));
        Error("RevisionConflict", () => registered.BeginAttempt(long.MaxValue, Guid.NewGuid()));
        Error("RevisionConflict", () => registered.Quarantine(2, TargetCreationQuarantineReason.Cancelled));
        var recorded = registered.BeginAttempt(1, Guid.NewGuid()).NewState;
        Error("InvalidId", () => recorded.BeginAttempt(1, Guid.Empty));
        Error("RevisionConflict", () => recorded.Quarantine(1, TargetCreationQuarantineReason.Cancelled));
        Error("RevisionConflict", () => recorded.Quarantine(3, TargetCreationQuarantineReason.Cancelled));
    }

    [Theory]
    [InlineData(TargetCreationQuarantineReason.OutcomeUnknown)]
    [InlineData(TargetCreationQuarantineReason.Cancelled)]
    [InlineData(TargetCreationQuarantineReason.AuthorityRejected)]
    [InlineData(TargetCreationQuarantineReason.TargetConflict)]
    public void QuarantineFromEitherNonterminalStatePreservesPinsAndAttemptAndIsTerminal(
        TargetCreationQuarantineReason reason)
    {
        var registered = TargetCreationOperation.Register(Pins());
        var recorded = registered.BeginAttempt(1, Guid.NewGuid()).NewState;
        foreach (var original in new[] { registered, recorded })
        {
            var terminal = original.Quarantine(original.Revision, reason);
            Assert.Equal(TargetCreationOperationState.ReconciliationRequired, terminal.State);
            Assert.Equal(original.Revision + 1, terminal.Revision);
            Assert.Equal(reason, terminal.QuarantineReason);
            Assert.Equal(original.AttemptId, terminal.AttemptId);
            Assert.Equal(original.AttemptExpectedRevision, terminal.AttemptExpectedRevision);
            Assert.Equal(original.Pins, terminal.Pins);
            Assert.NotSame(original.Pins, terminal.Pins);
            Assert.Null(original.QuarantineReason);
            Blocked(original);
            Blocked(terminal);
            // Stale exact repeats do not become idempotent successes, even with the same reason.
            Error("RevisionConflict", () => terminal.Quarantine(original.Revision, reason));
            Error("InvalidTransition", () => terminal.Quarantine(terminal.Revision, reason));
            Error("InvalidTransition", () => terminal.Quarantine(terminal.Revision,
                reason == TargetCreationQuarantineReason.Cancelled
                    ? TargetCreationQuarantineReason.OutcomeUnknown : TargetCreationQuarantineReason.Cancelled));
            if (original.AttemptId is Guid attempt)
            {
                var replay = terminal.BeginAttempt(original.AttemptExpectedRevision!.Value, attempt);
                Assert.False(replay.NewlyRecorded);
                Assert.Same(terminal, replay.NewState);
                Assert.Equal(TargetCreationOperationState.ReconciliationRequired, replay.NewState.State);
                Assert.Equal(reason, replay.NewState.QuarantineReason);
                Assert.Equal(terminal.Revision, replay.NewState.Revision);
                Blocked(replay.NewState);
                Error("RevisionConflict", () => terminal.BeginAttempt(1, Guid.NewGuid()));
            }
            else
                Error("RevisionConflict", () => terminal.BeginAttempt(1, recorded.AttemptId!.Value));
            Error("InvalidTransition", () => terminal.BeginAttempt(terminal.Revision, Guid.NewGuid()));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void UndefinedReasonsFailWithoutChangingAnyState(int reason)
    {
        var registered = TargetCreationOperation.Register(Pins());
        var recorded = registered.BeginAttempt(1, Guid.NewGuid()).NewState;
        var terminal = recorded.Quarantine(2, TargetCreationQuarantineReason.OutcomeUnknown);
        foreach (var operation in new[] { registered, recorded, terminal })
            Error("InvalidReason", () => operation.Quarantine(operation.Revision,
                (TargetCreationQuarantineReason)reason));
        Assert.Null(registered.QuarantineReason);
        Assert.Null(recorded.QuarantineReason);
        Assert.Equal(TargetCreationQuarantineReason.OutcomeUnknown, terminal.QuarantineReason);
    }

    [Fact]
    public void SnapshotsHaveNoPublicSettersOrPublicStateConstructorsAndDiagnosticsAreRedacted()
    {
        var pins = Pins();
        var registered = TargetCreationOperation.Register(pins);
        var receipt = registered.BeginAttempt(1, Guid.NewGuid());
        var terminal = receipt.NewState.Quarantine(2, TargetCreationQuarantineReason.TargetConflict);
        foreach (var type in new[] { typeof(TargetCreationOperationPins), typeof(TargetCreationOperation),
                     typeof(TargetCreationOperationAttemptReceipt) })
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Empty(typeof(TargetCreationOperation).GetConstructors());
        Assert.Empty(typeof(TargetCreationOperationAttemptReceipt).GetConstructors());
        foreach (var value in new object[] { pins, registered, receipt, receipt.NewState, terminal })
        {
            var diagnostic = value.ToString()!;
            Assert.Contains("details omitted", diagnostic);
            foreach (var property in typeof(TargetCreationOperationPins).GetProperties())
            {
                var pin = property.GetValue(pins)!;
                Assert.DoesNotContain(pin.ToString()!, diagnostic);
                if (pin is Guid id) Assert.DoesNotContain(id.ToString("N"), diagnostic);
            }
            Assert.DoesNotContain(receipt.NewState.AttemptId!.Value.ToString(), diagnostic);
        }
        var exception = Assert.Throws<DomainRuleException>(() =>
            receipt.NewState.BeginAttempt(1, Guid.NewGuid()));
        Assert.Equal("RevisionConflict", exception.Code);
        Assert.Equal("Creation revision conflict.", exception.Message);
        Assert.DoesNotContain(pins.OperationId.ToString(), exception.ToString());
    }
}
