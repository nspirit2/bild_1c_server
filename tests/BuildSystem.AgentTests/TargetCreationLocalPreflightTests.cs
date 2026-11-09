using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationLocalPreflightTests
{
    private static (TargetCreationRecord Record, TargetCreationPermitBinding Binding) Fixture()
    {
        var reservation = Guid.NewGuid(); var operation = Guid.NewGuid();
        var intent = new TargetCreationIntent(operation, Guid.NewGuid(), Guid.NewGuid(), 1, reservation,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "bs_test_" + reservation.ToString("N"), "bs_test_" + reservation.ToString("N"));
        var issued = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return (new(intent, TargetCreationState.IntentRecorded, 1, null, null, null),
            new(Guid.NewGuid(), operation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), issued, issued.AddMinutes(5))
            { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() });
    }
    private static TargetCreationRecord InFlight(TargetCreationRecord r, TargetCreationPermitBinding b) =>
        r with { State = TargetCreationState.CreationInFlight, Revision = 2, AttemptId = b.AttemptId, AttemptExpectedRevision = 1 };

    [Fact]
    public void Exact_initial_and_sole_inflight_are_inert_without_wallclock_authority()
    {
        var (r, b) = Fixture();
        foreach (var receipt in new[] { TargetCreationLocalPreflight.ValidateInitial(r, b), TargetCreationLocalPreflight.ValidateInFlight(InFlight(r, b), b) })
        {
            Assert.False(receipt.Executable); Assert.False(receipt.FreshAuthority); Assert.False(receipt.RetryAllowed);
            Assert.True(receipt.ScheduledJobsMustRemainBlocked); Assert.Same(b, receipt.Binding);
            Assert.Equal("TargetCreationLocalPreflightReceipt [NONEXECUTABLE]", receipt.ToString());
        }
    }
    [Fact]
    public void Cross_stage_attempt_and_quarantine_payloads_are_rejected()
    {
        var (r, b) = Fixture(); var flight = InFlight(r, b);
        foreach (var invalid in new[] { r, flight with { Revision = 1 }, flight with { AttemptId = Guid.NewGuid() },
            flight with { AttemptExpectedRevision = 2 }, flight with { QuarantineReason = "OutcomeUnknown" },
            flight with { State = TargetCreationState.ReconciliationRequired }, flight with { State = (TargetCreationState)99 },
            flight with { AttemptId = null }, flight with { AttemptExpectedRevision = null } })
            Assert.Throws<InvalidOperationException>(() => TargetCreationLocalPreflight.ValidateInFlight(invalid, b));
        foreach (var invalid in new[] { flight, r with { Revision = 2 }, r with { AttemptId = b.AttemptId },
            r with { AttemptExpectedRevision = 1 }, r with { QuarantineReason = "Cancelled" } })
            Assert.Throws<InvalidOperationException>(() => TargetCreationLocalPreflight.ValidateInitial(invalid, b));
    }
    [Fact]
    public void Legacy_or_mismatching_binding_and_bad_windows_never_pass()
    {
        var (r, b) = Fixture();
        foreach (var invalid in new[] { b with { CredentialId = null }, b with { CredentialId = Guid.Empty },
            b with { EndpointMappingId = null }, b with { EndpointMappingId = Guid.Empty }, b with { OperationId = Guid.NewGuid() },
            b with { PermitId = Guid.Empty }, b with { AttemptId = Guid.Empty }, b with { AgentId = Guid.Empty },
            b with { ArtifactId = Guid.Empty }, b with { RestoreIntentId = Guid.Empty },
            b with { IssuedAt = default }, b with { StartBefore = b.StartBefore.AddTicks(1) },
            b with { IssuedAt = b.IssuedAt.ToOffset(TimeSpan.FromHours(1)) },
            b with { StartBefore = b.StartBefore.ToOffset(TimeSpan.FromHours(1)) }, b with { StartBefore = b.IssuedAt } })
            Assert.Throws<InvalidOperationException>(() => TargetCreationLocalPreflight.ValidateInitial(r, invalid));
    }
    [Fact]
    public void Invalid_intent_and_null_objects_have_fixed_failure()
    {
        var (r, b) = Fixture();
        foreach (var intent in new[] { r.Intent with { OperationId = Guid.Empty }, r.Intent with { EnvironmentId = Guid.Empty },
            r.Intent with { JobId = Guid.Empty }, r.Intent with { ReservationId = Guid.Empty }, r.Intent with { TargetIncarnationId = Guid.Empty },
            r.Intent with { CanonicalClusterId = Guid.Empty }, r.Intent with { CanonicalSqlServerId = Guid.Empty },
            r.Intent with { FencingToken = 0 }, r.Intent with { FencingToken = -1 }, r.Intent with { InfobaseName = "secret-canary" },
            r.Intent with { SqlDatabaseName = r.Intent.SqlDatabaseName.ToUpperInvariant() } })
            Assert.Equal("InvalidTargetCreationLocalPreflight", Assert.Throws<InvalidOperationException>(() =>
                TargetCreationLocalPreflight.ValidateInitial(r with { Intent = intent }, b)).Message);
        Assert.Throws<InvalidOperationException>(() => TargetCreationLocalPreflight.ValidateInitial(null!, b));
        Assert.Throws<InvalidOperationException>(() => TargetCreationLocalPreflight.ValidateInitial(r, null!));
        Assert.Throws<InvalidOperationException>(() => TargetCreationLocalPreflight.ValidateInitial(r with { Intent = null! }, b));
    }
}