using BuildSystem.Domain;
using Xunit;

namespace BuildSystem.UnitTests;

public sealed class TargetCreationPermitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 11, 0, 0, TimeSpan.Zero);

    private static TargetCreationOperation Operation() => TargetCreationOperation.Register(new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

    private static TargetCreationPermit Permit() =>
        TargetCreationPermit.Prepare(Guid.NewGuid(), Operation(), Guid.NewGuid(), Now);

    private static void Error(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<DomainRuleException>(action).Code);

    private static void Blocked(TargetCreationPermit permit)
    {
        Assert.False(permit.Executable);
        Assert.True(permit.ScheduledJobsMustRemainBlocked);
    }

    [Fact]
    public void PrepareCopiesPinsAndBindsIdsAndOperationRevisionWithoutIssuingAuthority()
    {
        var operation = Operation();
        var id = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var permit = TargetCreationPermit.Prepare(id, operation, attempt, Now);
        Assert.Equal(id, permit.PermitId);
        Assert.Equal(attempt, permit.AttemptId);
        Assert.Equal(operation.Pins, permit.Pins);
        Assert.NotSame(operation.Pins, permit.Pins);
        Assert.Equal(1L, permit.ExpectedOperationRevision);
        Assert.Equal(1L, permit.Revision);
        Assert.Equal(TargetCreationPermitState.Prepared, permit.State);
        Assert.False(permit.HasRecordedStart);
        Assert.Null(permit.StartedAt);
        Assert.Null(permit.StartExpectedRevision);
        Assert.Equal(TargetCreationOperationState.Registered, operation.State);
        Assert.Null(operation.AttemptId);
        Blocked(permit);
    }

    [Fact]
    public void DeadlineIsExactlyFiveMinutesAndNeverAnExecutionLease()
    {
        var permit = Permit();
        Assert.Equal(Now, permit.IssuedAt);
        Assert.Equal(Now.AddMinutes(5), permit.StartBefore);
        Assert.Equal(TimeSpan.FromMinutes(5), permit.StartBefore - permit.IssuedAt);
        Blocked(permit);
    }

    [Fact]
    public void PrepareNormalizesOffsetWithoutChangingUtcInstant()
    {
        var permit = TargetCreationPermit.Prepare(Guid.NewGuid(), Operation(), Guid.NewGuid(),
            Now.ToOffset(TimeSpan.FromHours(5.5)));
        Assert.Equal(Now, permit.IssuedAt);
        Assert.Equal(TimeSpan.Zero, permit.IssuedAt.Offset);
        Assert.Equal(Now.AddMinutes(5), permit.StartBefore);
        Assert.Equal(TimeSpan.Zero, permit.StartBefore.Offset);
    }

    [Fact]
    public void EmptyPermitIdFails() => Error("InvalidId", () =>
        TargetCreationPermit.Prepare(Guid.Empty, Operation(), Guid.NewGuid(), Now));

    [Fact]
    public void EmptyBoundAttemptIdFails() => Error("InvalidId", () =>
        TargetCreationPermit.Prepare(Guid.NewGuid(), Operation(), Guid.Empty, Now));

    [Fact]
    public void NullOperationFails() => Error("InvalidOperation", () =>
        TargetCreationPermit.Prepare(Guid.NewGuid(), null!, Guid.NewGuid(), Now));

    [Fact]
    public void ExistingOperationGuardsRejectNullPinsAndEmptyPinIds()
    {
        Error("InvalidPins", () => TargetCreationOperation.Register(null!));
        for (var index = 0; index < 10; index++)
        {
            var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
            ids[index] = Guid.Empty;
            Error("InvalidId", () => new TargetCreationOperationPins(ids[0], ids[1], ids[2],
                ids[3], ids[4], ids[5], ids[6], ids[7], ids[8], ids[9]));
        }
    }

    [Fact]
    public void PreparationRejectsOperationWithRecordedAttempt()
    {
        var operation = Operation().BeginAttempt(1, Guid.NewGuid()).NewState;
        Error("InvalidTransition", () => TargetCreationPermit.Prepare(Guid.NewGuid(), operation,
            Guid.NewGuid(), Now));
    }

    [Fact]
    public void PreparationRejectsQuarantinedOperationsWithAndWithoutAttempt()
    {
        var original = Operation();
        foreach (var operation in new[] { original, original.BeginAttempt(1, Guid.NewGuid()).NewState })
        {
            var quarantined = operation.Quarantine(operation.Revision, TargetCreationQuarantineReason.Cancelled);
            Error("InvalidTransition", () => TargetCreationPermit.Prepare(Guid.NewGuid(), quarantined,
                Guid.NewGuid(), Now));
        }
    }

    [Fact]
    public void DeadlineOverflowIsFixedDomainError()
    {
        var error = Assert.Throws<DomainRuleException>(() => TargetCreationPermit.Prepare(
            Guid.NewGuid(), Operation(), Guid.NewGuid(), DateTimeOffset.MaxValue.AddMinutes(-5).AddTicks(1)));
        Assert.Equal("InvalidServerTime", error.Code);
        Assert.Equal("Server time cannot represent the start deadline.", error.Message);
    }

    [Fact]
    public void LastRepresentableFiveMinuteDeadlineIsAccepted()
    {
        var permit = TargetCreationPermit.Prepare(Guid.NewGuid(), Operation(), Guid.NewGuid(),
            DateTimeOffset.MaxValue.AddMinutes(-5));
        Assert.Equal(DateTimeOffset.MaxValue, permit.StartBefore);
    }

    [Fact]
    public void StartAtIssuanceIsInclusiveAndImmutable()
    {
        var permit = Permit();
        var receipt = permit.RecordStart(1, permit.AttemptId, Now);
        var started = receipt.NewState;
        Assert.True(receipt.NewlyRecorded);
        Assert.Equal(TargetCreationPermitState.StartRecorded, started.State);
        Assert.Equal(2L, started.Revision);
        Assert.True(started.HasRecordedStart);
        Assert.Equal(Now, started.StartedAt);
        Assert.Equal(1L, started.StartExpectedRevision);
        Assert.Equal(permit.Pins, started.Pins);
        Assert.NotSame(permit.Pins, started.Pins);
        Assert.Equal(permit.PermitId, started.PermitId);
        Assert.Equal(permit.AttemptId, started.AttemptId);
        Assert.Equal(permit.ExpectedOperationRevision, started.ExpectedOperationRevision);
        Assert.False(permit.HasRecordedStart);
        Assert.Equal(1L, permit.Revision);
        Assert.Equal(TargetCreationPermitState.Prepared, permit.State);
        Assert.False(receipt.Executable);
        Assert.True(receipt.ScheduledJobsMustRemainBlocked);
        Blocked(permit);
        Blocked(started);
    }

    [Fact]
    public void StartOneTickBeforeDeadlineSucceeds()
    {
        var permit = Permit();
        Assert.True(permit.RecordStart(1, permit.AttemptId, permit.StartBefore.AddTicks(-1)).NewlyRecorded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10000000)]
    public void FirstStartAtOrAfterExclusiveDeadlineFailsWithoutTransition(long ticks)
    {
        var permit = Permit();
        Error("PermitExpired", () => permit.RecordStart(1, permit.AttemptId, permit.StartBefore.AddTicks(ticks)));
        Assert.Equal(TargetCreationPermitState.Prepared, permit.State);
        Assert.Null(permit.StartedAt);
        Assert.Equal(1L, permit.Revision);
    }

    [Fact]
    public void OffsetEquivalentDeadlineIsStillExclusive()
    {
        var permit = Permit();
        Error("PermitExpired", () => permit.RecordStart(1, permit.AttemptId,
            permit.StartBefore.ToOffset(TimeSpan.FromHours(-7))));
        var started = permit.RecordStart(1, permit.AttemptId, Now.ToOffset(TimeSpan.FromHours(3))).NewState;
        Assert.Equal(Now, started.StartedAt);
        Assert.Equal(TimeSpan.Zero, started.StartedAt!.Value.Offset);
    }

    [Fact]
    public void ServerTimeBeforeIssuanceFailsEvenOnExactReplayOrRevoke()
    {
        var prepared = Permit();
        var started = prepared.RecordStart(1, prepared.AttemptId, Now).NewState;
        var revoked = started.Revoke(2, Now);
        foreach (var permit in new[] { prepared, started, revoked })
        {
            Error("InvalidServerTime", () => permit.RecordStart(1, permit.AttemptId, Now.AddTicks(-1)));
            Error("InvalidServerTime", () => permit.Revoke(permit.Revision, Now.AddTicks(-1)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void NonpositiveRevisionFailsInEveryState(long revision)
    {
        var prepared = Permit();
        var started = prepared.RecordStart(1, prepared.AttemptId, Now).NewState;
        foreach (var permit in new[] { prepared, started, started.Revoke(2, Now) })
        {
            Error("InvalidRevision", () => permit.RecordStart(revision, permit.AttemptId, Now));
            Error("InvalidRevision", () => permit.Revoke(revision, Now));
        }
    }

    [Fact]
    public void EmptyAndDifferentAttemptsNeverStartOrReplay()
    {
        var prepared = Permit();
        var started = prepared.RecordStart(1, prepared.AttemptId, Now).NewState;
        foreach (var permit in new[] { prepared, started, started.Revoke(2, Now), prepared.Revoke(1, Now) })
        {
            Error("InvalidId", () => permit.RecordStart(1, Guid.Empty, Now));
            foreach (var revision in new[] { 1L, permit.Revision })
                Error("AttemptMismatch", () => permit.RecordStart(revision, Guid.NewGuid(), Now));
        }
    }

    [Fact]
    public void FirstStartRequiresExactCurrentRevision()
    {
        var permit = Permit();
        Error("RevisionConflict", () => permit.RecordStart(2, permit.AttemptId, Now));
        Error("RevisionConflict", () => permit.RecordStart(long.MaxValue, permit.AttemptId, Now));
    }

    [Fact]
    public void OnlyOriginalAttemptRevisionPairReplaysAndNeverRenewsDeadline()
    {
        var prepared = Permit();
        var started = prepared.RecordStart(1, prepared.AttemptId, Now.AddMinutes(1)).NewState;
        foreach (var time in new[] { Now.AddMinutes(2), prepared.StartBefore, prepared.StartBefore.AddYears(1) })
        {
            var replay = started.RecordStart(1, started.AttemptId, time);
            Assert.False(replay.NewlyRecorded);
            Assert.Same(started, replay.NewState);
            Assert.False(replay.Executable);
            Assert.True(replay.ScheduledJobsMustRemainBlocked);
            Assert.Equal(prepared.StartBefore, replay.NewState.StartBefore);
            Assert.Equal(Now.AddMinutes(1), replay.NewState.StartedAt);
            Assert.Equal(2L, replay.NewState.Revision);
            Blocked(replay.NewState);
        }
        Error("InvalidTransition", () => started.RecordStart(2, started.AttemptId, Now));
        Error("RevisionConflict", () => started.RecordStart(3, started.AttemptId, Now));
    }

    [Fact]
    public void RevocationBeforeStartIsTerminalAndDoesNotFakeAReplay()
    {
        var prepared = Permit();
        var revoked = prepared.Revoke(1, Now);
        Assert.Equal(TargetCreationPermitState.Revoked, revoked.State);
        Assert.Equal(2L, revoked.Revision);
        Assert.False(revoked.HasRecordedStart);
        Assert.Null(revoked.StartedAt);
        Assert.Null(revoked.StartExpectedRevision);
        Assert.Equal(prepared.AttemptId, revoked.AttemptId);
        Error("RevisionConflict", () => revoked.RecordStart(1, revoked.AttemptId, Now));
        Error("InvalidTransition", () => revoked.RecordStart(2, revoked.AttemptId, Now));
        Assert.Equal(TargetCreationPermitState.Prepared, prepared.State);
        Blocked(revoked);
    }

    [Fact]
    public void RevocationAfterStartPreservesRecordedStartAndAllowsOnlyNonexecutingReplay()
    {
        var prepared = Permit();
        var started = prepared.RecordStart(1, prepared.AttemptId, Now.AddMinutes(1)).NewState;
        var revoked = started.Revoke(2, Now.AddMinutes(10));
        Assert.Equal(TargetCreationPermitState.Revoked, revoked.State);
        Assert.Equal(3L, revoked.Revision);
        Assert.Equal(started.StartedAt, revoked.StartedAt);
        Assert.Equal(started.StartExpectedRevision, revoked.StartExpectedRevision);
        Assert.Equal(started.AttemptId, revoked.AttemptId);
        Assert.True(revoked.HasRecordedStart);
        var replay = revoked.RecordStart(1, revoked.AttemptId, Now.AddYears(1));
        Assert.Same(revoked, replay.NewState);
        Assert.False(replay.NewlyRecorded);
        Assert.False(replay.Executable);
        Error("RevisionConflict", () => revoked.RecordStart(2, revoked.AttemptId, Now));
        Error("InvalidTransition", () => revoked.RecordStart(3, revoked.AttemptId, Now));
        Assert.Equal(TargetCreationPermitState.StartRecorded, started.State);
        Blocked(revoked);
    }

    [Fact]
    public void RevokeRequiresCurrentRevisionAndExactRepeatStillFails()
    {
        var prepared = Permit();
        var started = prepared.RecordStart(1, prepared.AttemptId, Now).NewState;
        foreach (var permit in new[] { prepared, started })
        {
            Error("RevisionConflict", () => permit.Revoke(permit.Revision + 1, Now));
            var revoked = permit.Revoke(permit.Revision, Now);
            Error("RevisionConflict", () => revoked.Revoke(permit.Revision, Now));
            Error("InvalidTransition", () => revoked.Revoke(revoked.Revision, Now));
        }
    }

    [Fact]
    public void AllTransitionsPreserveIssueTimeDeadlineAndCopiedPinsWithoutTtlReset()
    {
        var prepared = Permit();
        var started = prepared.RecordStart(1, prepared.AttemptId, Now.AddMinutes(4)).NewState;
        foreach (var permit in new[] { started, prepared.Revoke(1, Now.AddDays(1)), started.Revoke(2, Now.AddDays(1)) })
        {
            Assert.Equal(prepared.IssuedAt, permit.IssuedAt);
            Assert.Equal(prepared.StartBefore, permit.StartBefore);
            Assert.Equal(prepared.Pins, permit.Pins);
            Assert.NotSame(prepared.Pins, permit.Pins);
            Assert.Equal(prepared.PermitId, permit.PermitId);
            Assert.Equal(prepared.ExpectedOperationRevision, permit.ExpectedOperationRevision);
            Blocked(permit);
        }
    }

    [Fact]
    public void SnapshotsAndReceiptsHaveNoPublicSettersOrConstructorsAndFixedRedactedDiagnostics()
    {
        var permit = Permit();
        var receipt = permit.RecordStart(1, permit.AttemptId, Now);
        var revoked = receipt.NewState.Revoke(2, Now);
        foreach (var type in new[] { typeof(TargetCreationPermit), typeof(TargetCreationPermitStartReceipt) })
        {
            Assert.Empty(type.GetConstructors());
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        }
        foreach (var value in new object[] { permit, receipt.NewState, revoked })
            Assert.Equal("TargetCreationPermit [not execution authorization; details omitted]", value.ToString());
        Assert.Equal("TargetCreationPermitStartReceipt [not execution authorization; details omitted]", receipt.ToString());
        foreach (var value in new object[] { permit, receipt, revoked })
        {
            Assert.DoesNotContain(permit.PermitId.ToString(), value.ToString()!);
            Assert.DoesNotContain(permit.AttemptId.ToString(), value.ToString()!);
            foreach (var property in typeof(TargetCreationOperationPins).GetProperties())
                Assert.DoesNotContain(property.GetValue(permit.Pins)!.ToString()!, value.ToString()!);
        }
    }
}
