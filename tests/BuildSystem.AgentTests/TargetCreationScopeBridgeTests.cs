using BuildSystem.Agent;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationScopeBridgeTests
{
    private static (TargetCreationRecord Record, TargetCreationPermitBinding Binding, Guid RacMap, Guid SqlMap) Fixture()
    {
        var reservation = Guid.NewGuid(); var operation = Guid.NewGuid();
        var name = "bs_test_" + reservation.ToString("N");
        var intent = new TargetCreationIntent(operation, Guid.NewGuid(), Guid.NewGuid(), 17, reservation,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
        var issued = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var binding = new TargetCreationPermitBinding(Guid.NewGuid(), operation, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), issued, issued.AddMinutes(5))
            { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
        return (new(intent, TargetCreationState.CreationInFlight, 2, binding.AttemptId, 1, null),
            binding, Guid.NewGuid(), Guid.NewGuid());
    }

    private static TargetCreationScopeBridgeReceipt Prepare(TargetCreationRecord record,
        TargetCreationPermitBinding binding, Guid racMap, Guid sqlMap) =>
        TargetCreationScopeBridge.Prepare(record, binding, binding.AgentId, record.Intent.CanonicalClusterId,
            record.Intent.CanonicalSqlServerId, binding.EndpointMappingId!.Value, racMap, sqlMap);

    private static void Invalid(Action action)
    {
        var ex = Assert.Throws<TargetCreationScopeBridgeException>(action);
        Assert.Equal("InvalidTargetCreationScopeBridge", ex.Message);
        Assert.Equal(ex.Message, ex.Code);
        Assert.Null(ex.InnerException);
        Assert.Equal("InvalidTargetCreationScopeBridge", ex.ToString());
    }

    [Fact]
    public void Projects_exact_shared_creation_pair_and_independent_observation_pins_without_authority()
    {
        var (record, binding, racMap, sqlMap) = Fixture();
        var receipt = Prepare(record, binding, racMap, sqlMap);
        var intent = record.Intent;
        Assert.Same(record, receipt.Record); Assert.Same(binding, receipt.Binding);
        Assert.Equal(intent.OperationId, receipt.CreationScope.OperationId);
        Assert.Equal(binding.PermitId, receipt.CreationScope.PermitId);
        Assert.Equal(binding.AttemptId, receipt.CreationScope.AttemptId);
        Assert.Equal(intent.ReservationId, receipt.CreationScope.ReservationId);
        Assert.Equal(intent.TargetIncarnationId, receipt.CreationScope.TargetIncarnationId);
        Assert.Equal(intent.CanonicalClusterId, receipt.CreationScope.CanonicalClusterId);
        Assert.Equal(intent.CanonicalSqlServerId, receipt.CreationScope.CanonicalSqlServerId);
        Assert.Equal(binding.EndpointMappingId, receipt.CreationScope.EndpointMappingId);
        Assert.Equal(intent.OperationId, receipt.NamespaceScope.OperationId);
        Assert.Equal(binding.AttemptId, receipt.NamespaceScope.AttemptId);
        Assert.Equal(intent.ReservationId, receipt.NamespaceScope.ReservationId);
        Assert.Equal(intent.CanonicalSqlServerId, receipt.NamespaceScope.CanonicalSqlServerId);
        Assert.Equal(receipt.CreationScope.EndpointMappingId, receipt.NamespaceScope.EndpointMappingId);
        Assert.Equal(intent.OperationId, receipt.PresenceRequest.OperationId);
        Assert.Equal(intent.FencingToken, receipt.PresenceRequest.FencingToken);
        Assert.Equal(intent.TargetIncarnationId, receipt.PresenceRequest.TargetIncarnationId);
        Assert.Equal(intent.ReservationId, receipt.PresenceRequest.ReservationId);
        Assert.Equal(intent.CanonicalClusterId, receipt.PresenceRequest.CanonicalClusterId);
        Assert.Equal(intent.CanonicalSqlServerId, receipt.PresenceRequest.CanonicalSqlServerId);
        Assert.Equal(racMap, receipt.PresenceRequest.RacEndpointMappingId);
        Assert.Equal(sqlMap, receipt.PresenceRequest.SqlEndpointMappingId);
        Assert.False(receipt.Executable); Assert.False(receipt.FreshAuthority);
        Assert.False(receipt.RetryAllowed); Assert.True(receipt.ScheduledJobsMustRemainBlocked);
        Assert.Equal("TargetCreationScopeBridgeReceipt [NONEXECUTABLE]", receipt.ToString());
        Assert.DoesNotContain(intent.ReservationId.ToString(), receipt.ToString());
        Assert.DoesNotContain(binding.CredentialId!.Value.ToString(), receipt.ToString());
    }

    [Fact]
    public void Initial_quarantined_wrong_attempt_and_bad_fence_cannot_project()
    {
        var (record, binding, racMap, sqlMap) = Fixture();
        foreach (var invalid in new[] { record with { State = TargetCreationState.IntentRecorded, Revision = 1,
                AttemptId = null, AttemptExpectedRevision = null },
            record with { State = TargetCreationState.ReconciliationRequired, QuarantineReason = "OutcomeUnknown" },
            record with { QuarantineReason = "Cancelled" }, record with { Revision = 1 },
            record with { AttemptId = Guid.NewGuid() }, record with { AttemptExpectedRevision = 2 },
            record with { Intent = record.Intent with { FencingToken = 0 } },
            record with { Intent = record.Intent with { InfobaseName = "secret-canary" } },
            record with { Intent = record.Intent with { SqlDatabaseName = "secret-canary" } } })
            Invalid(() => Prepare(invalid, binding, racMap, sqlMap));
    }

    [Fact]
    public void Wrong_or_missing_expected_trusted_identity_is_rejected()
    {
        var (record, binding, racMap, sqlMap) = Fixture(); var intent = record.Intent;
        foreach (var bad in new[] { Guid.NewGuid(), Guid.Empty })
        {
            Invalid(() => TargetCreationScopeBridge.Prepare(record, binding, bad, intent.CanonicalClusterId,
                intent.CanonicalSqlServerId, binding.EndpointMappingId!.Value, racMap, sqlMap));
            Invalid(() => TargetCreationScopeBridge.Prepare(record, binding, binding.AgentId, bad,
                intent.CanonicalSqlServerId, binding.EndpointMappingId!.Value, racMap, sqlMap));
            Invalid(() => TargetCreationScopeBridge.Prepare(record, binding, binding.AgentId, intent.CanonicalClusterId,
                bad, binding.EndpointMappingId!.Value, racMap, sqlMap));
            Invalid(() => TargetCreationScopeBridge.Prepare(record, binding, binding.AgentId, intent.CanonicalClusterId,
                intent.CanonicalSqlServerId, bad, racMap, sqlMap));
        }
        Invalid(() => Prepare(record, binding, Guid.Empty, sqlMap));
        Invalid(() => Prepare(record, binding, racMap, Guid.Empty));
    }

    [Fact]
    public void Credential_mapping_and_binding_mismatch_are_not_silently_repaired()
    {
        var (record, binding, racMap, sqlMap) = Fixture();
        foreach (var invalid in new[] { binding with { EndpointMappingId = null },
            binding with { EndpointMappingId = Guid.Empty }, binding with { CredentialId = null },
            binding with { CredentialId = Guid.Empty }, binding with { AgentId = Guid.NewGuid() },
            binding with { OperationId = Guid.NewGuid() }, binding with { AttemptId = Guid.NewGuid() } })
            Invalid(() => TargetCreationScopeBridge.Prepare(record, invalid, binding.AgentId,
                record.Intent.CanonicalClusterId, record.Intent.CanonicalSqlServerId,
                binding.EndpointMappingId!.Value, racMap, sqlMap));
        Invalid(() => TargetCreationScopeBridge.Prepare(null!, binding, binding.AgentId,
            record.Intent.CanonicalClusterId, record.Intent.CanonicalSqlServerId,
            binding.EndpointMappingId!.Value, racMap, sqlMap));
        Invalid(() => TargetCreationScopeBridge.Prepare(record, null!, binding.AgentId,
            record.Intent.CanonicalClusterId, record.Intent.CanonicalSqlServerId,
            binding.EndpointMappingId!.Value, racMap, sqlMap));
    }

    [Fact]
    public void Observation_submapping_ids_need_not_be_credential_pair_or_each_other()
    {
        var (record, binding, _, _) = Fixture();
        var shared = binding.EndpointMappingId!.Value;
        var receipt = Prepare(record, binding, shared, shared);
        Assert.Equal(shared, receipt.PresenceRequest.RacEndpointMappingId);
        Assert.Equal(shared, receipt.PresenceRequest.SqlEndpointMappingId);
        Assert.Equal(shared, receipt.NamespaceScope.EndpointMappingId);
        Assert.False(receipt.Executable);
    }
}
