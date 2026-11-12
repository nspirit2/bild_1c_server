using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationRegistrationBridgeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "registration-bridge-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Issued = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private const string SqlServer = "trusted-sql.example";
    private readonly Guid racMapping = Guid.NewGuid();
    private readonly Guid sqlMapping = Guid.NewGuid();
    private readonly Guid observation = Guid.NewGuid();

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid();
        var name = "bs_test_" + reservation.ToString("N");
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, reservation,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
    }

    private static TargetCreationPermitBinding Binding(TargetCreationIntent intent) => new(
        Guid.NewGuid(), intent.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Issued, Issued.AddMinutes(5))
    { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };

    private static TargetCreationAcknowledgement Ack(TargetCreationIntent intent, TargetCreationPermitBinding binding) =>
        new(intent, binding, Guid.NewGuid(), Guid.NewGuid(), Issued.AddSeconds(12), binding.StartBefore);

    private static void Begin(AgentJournal journal, TargetCreationIntent intent, TargetCreationPermitBinding binding)
    {
        journal.SaveTargetCreationIntent(intent);
        journal.SaveTargetCreationPermitBinding(intent, binding);
        journal.TryBeginTargetCreation(intent.OperationId, 1, binding.AttemptId);
    }

    private TargetCreationRegistrationBridgeReceipt Prepare(AgentJournal journal, TargetCreationIntent intent,
        TargetCreationPermitBinding binding, Guid? operation = null, Guid? agent = null, Guid? cluster = null,
        Guid? sql = null, Guid? pair = null, Guid? rac = null, Guid? sqlObservation = null,
        string? sqlServer = null, Guid? observationId = null) =>
        TargetCreationRegistrationBridge.Prepare(journal, operation ?? intent.OperationId, agent ?? binding.AgentId,
            cluster ?? intent.CanonicalClusterId, sql ?? intent.CanonicalSqlServerId,
            pair ?? binding.EndpointMappingId!.Value, rac ?? racMapping,
            sqlObservation ?? sqlMapping, sqlServer ?? SqlServer, observationId ?? observation);

    private static void AssertUnavailable(Action action)
    {
        var error = Assert.Throws<TargetCreationRegistrationBridgeUnavailableException>(action);
        Assert.Equal("TargetCreationRegistrationBridgeUnavailable", error.Code);
        Assert.Null(error.InnerException);
        Assert.Equal(error.Code, error.ToString());
    }

    [Fact]
    public void PersistedAcknowledgementProducesExactReadOnlyScopeAfterReopen()
    {
        var intent = Intent(); var binding = Binding(intent); var ack = Ack(intent, binding);
        using (var journal = new AgentJournal(directory))
        {
            Begin(journal, intent, binding);
            journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, ack);
        }
        using var reopened = new AgentJournal(directory);
        var record = reopened.GetTargetCreationIntent(intent.OperationId);
        var receipt = Prepare(reopened, intent, binding);
        var scope = receipt.Scope;
        Assert.Equal(ack, receipt.Acknowledgement);
        Assert.Equal(intent.OperationId, scope.OperationId);
        Assert.Equal(intent.FencingToken, scope.FencingToken);
        Assert.Equal(intent.TargetIncarnationId, scope.TargetIncarnationId);
        Assert.Equal(intent.ReservationId, scope.ReservationId);
        Assert.Equal(intent.CanonicalClusterId, scope.CanonicalClusterId);
        Assert.Equal(intent.CanonicalSqlServerId, scope.CanonicalSqlServerId);
        Assert.Equal(racMapping, scope.RacEndpointMappingId);
        Assert.Equal(sqlMapping, scope.SqlEndpointMappingId);
        Assert.Equal(binding.AttemptId, scope.AttemptId);
        Assert.Equal(ack.ClaimId, scope.ClaimId);
        Assert.Equal(ack.ReportedInfobaseId, scope.InfobaseId);
        Assert.Equal(observation, scope.ObservationId);
        Assert.Equal(intent.InfobaseName, scope.ExpectedInfobaseName);
        Assert.Equal(SqlServer, scope.ExpectedSqlServer);
        Assert.Equal(intent.SqlDatabaseName, scope.ExpectedDatabaseName);
        Assert.False(receipt.Executable); Assert.False(receipt.FreshAuthority);
        Assert.False(receipt.OwnershipEstablished); Assert.False(receipt.RestoreAllowed);
        Assert.False(receipt.RetryAllowed); Assert.True(receipt.ScheduledJobsMustRemainBlocked);
        Assert.True(receipt.RequiresIndependentVerification);
        Assert.DoesNotContain(ack.ReportedInfobaseId.ToString(), receipt.ToString());
        Assert.DoesNotContain(SqlServer, receipt.ToString());
        Assert.Equal(record, reopened.GetTargetCreationIntent(intent.OperationId));
        Assert.Equal(ack, reopened.GetTargetCreationAcknowledgement(intent.OperationId));
        // The old claim's expiry is historical, not a renewed lease or freshness guarantee.
        Assert.Equal(Issued.AddMinutes(5), receipt.Acknowledgement.ClaimExpiresAt);
    }

    [Fact]
    public void PublicAcknowledgementAloneCannotBeUsedWithoutPersistingIt()
    {
        var intent = Intent(); var binding = Binding(intent); var publicAck = Ack(intent, binding);
        using var journal = new AgentJournal(directory);
        Begin(journal, intent, binding);
        var before = journal.GetTargetCreationIntent(intent.OperationId);
        AssertUnavailable(() => Prepare(journal, intent, binding));
        Assert.Equal(before, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(journal.GetTargetCreationAcknowledgement(intent.OperationId));
        Assert.NotEqual(Guid.Empty, publicAck.ReportedInfobaseId);
    }

    [Fact]
    public void OrdinaryQuarantineHasNoBridgeEvenAfterRestart()
    {
        var intent = Intent(); var binding = Binding(intent);
        using (var journal = new AgentJournal(directory))
        {
            Begin(journal, intent, binding);
            journal.QuarantineTargetCreation(intent.OperationId, 2, "OutcomeUnknown");
        }
        using var reopened = new AgentJournal(directory);
        var before = reopened.GetTargetCreationIntent(intent.OperationId);
        AssertUnavailable(() => Prepare(reopened, intent, binding));
        Assert.Equal(before, reopened.GetTargetCreationIntent(intent.OperationId));
        Assert.Null(reopened.GetTargetCreationAcknowledgement(intent.OperationId));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("agent")]
    [InlineData("cluster")]
    [InlineData("sql")]
    [InlineData("pair")]
    [InlineData("rac")]
    [InlineData("sqlObservation")]
    [InlineData("observation")]
    [InlineData("server")]
    [InlineData("serverWhitespace")]
    [InlineData("serverOption")]
    [InlineData("serverLong")]
    public void InvalidOrMismatchedTrustedConfigurationFailsWithoutChangingJournal(string change)
    {
        var intent = Intent(); var binding = Binding(intent); var ack = Ack(intent, binding);
        using var journal = new AgentJournal(directory);
        Begin(journal, intent, binding);
        journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, ack);
        var before = journal.GetTargetCreationIntent(intent.OperationId);
        var unrelated = Guid.NewGuid();
        AssertUnavailable(() => { _ = change switch
        {
            "operation" => Prepare(journal, intent, binding, operation: unrelated),
            "agent" => Prepare(journal, intent, binding, agent: unrelated),
            "cluster" => Prepare(journal, intent, binding, cluster: unrelated),
            "sql" => Prepare(journal, intent, binding, sql: unrelated),
            "pair" => Prepare(journal, intent, binding, pair: unrelated),
            "rac" => Prepare(journal, intent, binding, rac: Guid.Empty),
            "sqlObservation" => Prepare(journal, intent, binding, sqlObservation: Guid.Empty),
            "observation" => Prepare(journal, intent, binding, observationId: Guid.Empty),
            "server" => Prepare(journal, intent, binding, sqlServer: ""),
            "serverWhitespace" => Prepare(journal, intent, binding, sqlServer: "trusted sql"),
            "serverOption" => Prepare(journal, intent, binding, sqlServer: "-server"),
            _ => Prepare(journal, intent, binding, sqlServer: new string('x', 256))
        }; });
        Assert.Equal(before, journal.GetTargetCreationIntent(intent.OperationId));
        Assert.Equal(ack, journal.GetTargetCreationAcknowledgement(intent.OperationId));
    }

    [Fact]
    public void IndependentObservationMappingsAreNotConflatedWithCredentialPairMapping()
    {
        var intent = Intent(); var binding = Binding(intent); var ack = Ack(intent, binding);
        using var journal = new AgentJournal(directory);
        Begin(journal, intent, binding);
        journal.QuarantineTargetCreationWithAcknowledgement(intent.OperationId, 2, ack);
        var receipt = Prepare(journal, intent, binding);
        Assert.NotEqual(binding.EndpointMappingId, receipt.Scope.RacEndpointMappingId);
        Assert.NotEqual(binding.EndpointMappingId, receipt.Scope.SqlEndpointMappingId);
        Assert.NotEqual(receipt.Scope.RacEndpointMappingId, receipt.Scope.SqlEndpointMappingId);
    }
}
