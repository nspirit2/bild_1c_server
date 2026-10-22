using BuildSystem.Domain;
using Xunit;

namespace BuildSystem.UnitTests;

public sealed class TestEnvironmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static EnvironmentReservation Reservation() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        100, new("sql-server", "unique-db-slot"), new("cluster", "unique-infobase-slot"));
    private static TestEnvironment New(EnvironmentKind kind = EnvironmentKind.Restored, bool autoDelete = false) =>
        new(Guid.NewGuid(), Reservation(), new(TimeSpan.FromDays(7), TimeSpan.FromDays(30),
            autoDelete ? TimeSpan.FromDays(3) : null), new(kind, "sha256:source", "1.2.3", "profile@2"), Now);
    private static TestEnvironment Preparing(EnvironmentKind kind = EnvironmentKind.Restored, bool autoDelete = false)
    {
        var env = New(kind, autoDelete);
        env.StartProvisioning(Guid.NewGuid(), 1, Now.AddHours(1));
        env.StartPreparing(Now.AddHours(2));
        return env;
    }
    private static VerificationEvidence Verify(TestEnvironment env, DateTimeOffset? time = null) => new(
        env.Reservation.Id, env.ProvisioningJobId!.Value, env.ProvisioningAttempt!.Value,
        time ?? Now.AddHours(3), env.Reservation.SqlDatabase, env.Reservation.Infobase,
        "sha256:source", "1.2.3", "profile@2", CheckOutcome.Passed, CheckOutcome.Passed,
        CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Passed,
        CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Passed);
    private static TestEnvironment Ready(bool autoDelete = false)
    {
        var env = Preparing(autoDelete: autoDelete);
        env.PublishReady(Verify(env), Now.AddHours(3));
        return env;
    }
    private static DeletionSafetyEvidence Safety(TestEnvironment env, DateTimeOffset now) => new(
        env.Reservation.Id, env.Reservation.SqlDatabase, env.Reservation.Infobase, now, "journal/safety",
        CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Passed);
    private static DeletionEvidence Deleted(TestEnvironment env, DateTimeOffset now) => new(
        env.Reservation.Id, env.DeletionJobId!.Value, now, env.Reservation.SqlDatabase,
        env.Reservation.Infobase, "journal/deletion", CheckOutcome.Passed, CheckOutcome.Passed,
        CheckOutcome.Passed, CheckOutcome.Passed);
    private static void Error(string code, Action action) => Assert.Equal(code,
        Assert.Throws<DomainRuleException>(action).Code);

    [Fact]
    public void TtlStartsFromReadyNotRequestAndAutoDeletionIsOptIn()
    {
        var env = Ready();
        Assert.Equal(Now.AddHours(3), env.ReadyAt);
        Assert.Equal(Now.AddHours(3).AddDays(7), env.ExpiresAt);
        Assert.Null(env.DeleteAfter);
        Assert.True(env.HoldsQuota);
        var automatic = Ready(true);
        Assert.Equal(automatic.ExpiresAt!.Value.AddDays(3), automatic.DeleteAfter);
    }

    [Fact]
    public void ExpiryIsIdempotentAndDoesNotReleaseQuota()
    {
        var env = Ready();
        Assert.False(env.Expire(env.Version, env.ExpiresAt!.Value.AddTicks(-1)));
        Assert.True(env.Expire(env.Version, env.ExpiresAt.Value));
        var version = env.Version;
        Assert.False(env.Expire(version, env.ExpiresAt.Value));
        Assert.Equal(version, env.Version);
        Assert.Equal(EnvironmentState.Expired, env.State);
        Assert.True(env.HoldsQuota);
    }

    [Fact]
    public void ExpiredCanBeExtendedRepeatedlyFromNowWithinPolicy()
    {
        var env = Ready(true);
        var later = env.ExpiresAt!.Value.AddDays(1);
        env.Expire(env.Version, later);
        env.Extend(env.Version, TimeSpan.FromDays(30), later);
        Assert.Equal(later.AddDays(30), env.ExpiresAt);
        Assert.Equal(later.AddDays(33), env.DeleteAfter);
        env.Extend(env.Version, TimeSpan.FromDays(30), later.AddDays(29));
        Assert.Equal(later.AddDays(59), env.ExpiresAt);
        Error("InvalidTtl", () => env.Extend(env.Version, TimeSpan.FromDays(31), later.AddDays(30)));
    }

    [Fact]
    public void ExtendAndDeleteRejectStaleVersionAndCannotBothWin()
    {
        var env = Ready();
        var version = env.Version;
        var time = Now.AddDays(1);
        env.Extend(version, TimeSpan.FromDays(30), time);
        Error("VersionConflict", () => env.BeginDeletion(version, Guid.NewGuid(), Safety(env, time), time));
        var job = env.BeginDeletion(env.Version, Guid.NewGuid(), Safety(env, time), time);
        Assert.Equal(job, env.BeginDeletion(env.Version, Guid.NewGuid(), Safety(env, time), time));
        Error("InvalidTransition", () => env.Extend(env.Version, TimeSpan.FromDays(30), time));
    }

    [Fact]
    public void NoGenericStateSetterAndInvalidTransitionsFail()
    {
        var env = New();
        Error("InvalidTransition", () => env.StartPreparing(Now));
        env.StartProvisioning(Guid.NewGuid(), 1, Now);
        Error("InvalidTransition", () => env.BeginDeletion(env.Version, Guid.NewGuid(), Safety(env, Now), Now));
        Error("InvalidTransition", () => env.Extend(env.Version, TimeSpan.FromDays(7), Now));
    }

    [Fact]
    public void UtcAndMonotonicInjectedTimesAreRequired()
    {
        var env = New();
        Error("UtcRequired", () => env.StartProvisioning(Guid.NewGuid(), 1, Now.ToOffset(TimeSpan.FromHours(3))));
        Error("TimeWentBackwards", () => env.StartProvisioning(Guid.NewGuid(), 1, Now.AddTicks(-1)));
        Assert.Equal(EnvironmentState.Requested, env.State);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("attempt")]
    [InlineData("reservation")]
    [InlineData("resource")]
    [InlineData("source")]
    [InlineData("profile")]
    [InlineData("configuration")]
    public void VerifyIsBoundToCurrentAttemptResourcesAndPinnedRequirements(string field)
    {
        var env = Preparing();
        var evidence = Verify(env);
        evidence = field switch
        {
            "job" => evidence with { JobId = Guid.NewGuid() },
            "attempt" => evidence with { Attempt = 2 },
            "reservation" => evidence with { ReservationId = Guid.NewGuid() },
            "resource" => evidence with { SqlDatabase = new("sql-server", "foreign-db") },
            "source" => evidence with { SourceFingerprint = "other" },
            "profile" => evidence with { ProfileVersion = "other" },
            _ => evidence with { ConfigurationVersion = "other" }
        };
        Error("EvidenceMismatch", () => env.PublishReady(evidence, Now.AddHours(3)));
        Assert.Null(env.ReadyAt);
    }

    [Fact]
    public void MissingIsolationAndFailedApplicationCheckCannotPublish()
    {
        var env = Preparing();
        Error("VerificationIncomplete", () => env.PublishReady(Verify(env) with
            { InfrastructureIsolation = CheckOutcome.Unknown }, Now.AddHours(3)));
        Error("VerificationIncomplete", () => env.PublishReady(Verify(env) with
            { SafeApplicationScenario = CheckOutcome.Failed }, Now.AddHours(3)));
        Error("StaleEvidence", () => env.PublishReady(Verify(env, Now.AddHours(1)), Now.AddHours(3)));
        Error("StaleEvidence", () => env.PublishReady(Verify(env, Now.AddHours(4)), Now.AddHours(3)));
    }

    [Fact]
    public void EmptyRequiresPreparationButDoesNotPromiseApplicationScenario()
    {
        var env = Preparing(EnvironmentKind.Empty);
        env.PublishReady(Verify(env) with { ApplicationControlData = CheckOutcome.Unknown,
            SafeApplicationScenario = CheckOutcome.Unknown }, Now.AddHours(3));
        Assert.Equal(EnvironmentState.Ready, env.State);
    }

    [Fact]
    public void ReconciliationRequiresFreshVerifyAndDoesNotRestartLifetime()
    {
        var env = Ready();
        var deadline = env.ExpiresAt;
        env.RequireAttention("UnknownOutcome", Now.AddDays(1));
        env.ResumeVerification(Guid.NewGuid(), 2, Now.AddDays(2));
        Error("StaleEvidence", () => env.PublishReady(Verify(env), Now.AddDays(2)));
        env.PublishReady(Verify(env, Now.AddDays(2)), Now.AddDays(2));
        Assert.Equal(deadline, env.ExpiresAt);
        Assert.Equal(Now.AddHours(3), env.ReadyAt);
    }

    [Fact]
    public void FailedCreationReleasesQuotaOnlyAfterBothResourcesConfirmedAbsent()
    {
        var env = New();
        env.StartProvisioning(Guid.NewGuid(), 1, Now);
        env.Fail("RestoreFailed", Now);
        Assert.True(env.HoldsQuota);
        env.BeginDeletion(env.Version, Guid.NewGuid(), Safety(env, Now), Now);
        Error("DeletionUnconfirmed", () => env.ConfirmDeleted(Deleted(env, Now) with
            { InfobaseAbsent = CheckOutcome.Unknown }, Now));
        Assert.True(env.HoldsQuota);
        Error("DeletionUnconfirmed", () => env.ConfirmDeleted(Deleted(env, Now) with
            { SqlDatabaseAbsent = CheckOutcome.Failed }, Now));
        Error("EvidenceMismatch", () => env.ConfirmDeleted(Deleted(env, Now) with
            { SqlDatabase = new("sql-server", "same-name-foreign-incarnation") }, Now));
        env.ConfirmDeleted(Deleted(env, Now), Now);
        Assert.False(env.HoldsQuota);
        Assert.Equal(EnvironmentState.Deleted, env.State);
        Error("InvalidTransition", () => env.RequireAttention("No", Now));
    }

    [Fact]
    public void DeletionRequiresSettledOwnershipAndNoActiveSessions()
    {
        var env = Ready();
        var now = Now.AddDays(1);
        Error("UnsafeDeletion", () => env.BeginDeletion(env.Version, Guid.NewGuid(),
            Safety(env, now) with { OwnershipAndCreationHistory = CheckOutcome.Unknown }, now));
        Error("UnsafeDeletion", () => env.BeginDeletion(env.Version, Guid.NewGuid(),
            Safety(env, now) with { NoConflictingOperation = CheckOutcome.Failed }, now));
        Error("UnsafeDeletion", () => env.BeginDeletion(env.Version, Guid.NewGuid(),
            Safety(env, now) with { SessionsDrained = CheckOutcome.Failed }, now));
    }

    [Fact]
    public void DeletionRetryUsesNewJobAndRejectsOldConfirmation()
    {
        var env = Ready();
        var now = Now.AddDays(1);
        env.BeginDeletion(env.Version, Guid.NewGuid(), Safety(env, now), now);
        var oldEvidence = Deleted(env, now);
        env.RequireAttention("DeleteUncertain", now);
        Error("DeletionStarted", () => env.ResumeVerification(Guid.NewGuid(), 2, now));
        var newJob = Guid.NewGuid();
        Assert.Equal(newJob, env.BeginDeletion(env.Version, newJob, Safety(env, now), now));
        Error("EvidenceMismatch", () => env.ConfirmDeleted(oldEvidence, now));
        env.ConfirmDeleted(Deleted(env, now), now);
        Assert.False(env.HoldsQuota);
    }
}
