namespace BuildSystem.Agent;

/// <summary>Validated local metadata only: never persistence, lease freshness or execution authority.</summary>
public sealed record TargetCreationLocalPreflightReceipt(TargetCreationRecord Record, TargetCreationPermitBinding Binding)
{
    public bool Executable => false;
    public bool FreshAuthority => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool RetryAllowed => false;
    public override string ToString() => "TargetCreationLocalPreflightReceipt [NONEXECUTABLE]";
}

/// <summary>Pure correlation of caller-supplied objects. Does not read/write a journal or prove its contents.
/// Distinguishes an initial local slot shape from the sole in-flight attempt shape; neither authorizes CREATE.</summary>
public static class TargetCreationLocalPreflight
{
    public static TargetCreationLocalPreflightReceipt ValidateInitial(TargetCreationRecord record,
        TargetCreationPermitBinding binding) => Validate(record, binding, false);

    public static TargetCreationLocalPreflightReceipt ValidateInFlight(TargetCreationRecord record,
        TargetCreationPermitBinding binding) => Validate(record, binding, true);

    private static TargetCreationLocalPreflightReceipt Validate(TargetCreationRecord record,
        TargetCreationPermitBinding binding, bool inFlight)
    {
        if (record is null || record.Intent is null || binding is null) throw Invalid();
        var intent = record.Intent;
        if (new[] { intent.OperationId, intent.EnvironmentId, intent.JobId, intent.ReservationId,
                intent.TargetIncarnationId, intent.CanonicalClusterId, intent.CanonicalSqlServerId,
                binding.PermitId, binding.OperationId, binding.AttemptId, binding.RestoreIntentId,
                binding.ArtifactId, binding.AgentId }.Any(id => id == Guid.Empty) || intent.FencingToken <= 0 ||
            intent.OperationId != binding.OperationId ||
            intent.InfobaseName != "bs_test_" + intent.ReservationId.ToString("N") ||
            intent.SqlDatabaseName != "bs_test_" + intent.ReservationId.ToString("N") ||
            binding.CredentialId is not { } credential || credential == Guid.Empty ||
            binding.EndpointMappingId is not { } mapping || mapping == Guid.Empty ||
            binding.IssuedAt.Offset != TimeSpan.Zero || binding.StartBefore.Offset != TimeSpan.Zero ||
            binding.IssuedAt == default || binding.StartBefore - binding.IssuedAt != TimeSpan.FromMinutes(5) ||
            record.QuarantineReason is not null) throw Invalid();
        if (inFlight)
        {
            if (record.State != TargetCreationState.CreationInFlight || record.Revision != 2 ||
                record.AttemptId != binding.AttemptId || record.AttemptExpectedRevision != 1) throw Invalid();
        }
        else if (record.State != TargetCreationState.IntentRecorded || record.Revision != 1 ||
                 record.AttemptId is not null || record.AttemptExpectedRevision is not null) throw Invalid();
        return new(record, binding);
    }

    private static InvalidOperationException Invalid() => new("InvalidTargetCreationLocalPreflight");
}