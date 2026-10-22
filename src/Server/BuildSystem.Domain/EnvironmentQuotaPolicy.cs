namespace BuildSystem.Domain;

public sealed record EnvironmentQuotaLimits(
    int MaxActiveEnvironments, int MaxConcurrentProvisioning, long MaxStorageBytes, TimeSpan MaxTtl);

public sealed record PoolBudget(bool Enabled, long CapacityBytes, long MaxEnvironmentBytes,
    int MaxConcurrentProvisioning);

/// <summary>Snapshot row for every environment, not only active jobs or successful resources.</summary>
public sealed record EnvironmentQuotaEntry(Guid ReservationId, Guid OwnerId, Guid PoolId,
    long ReservedBytes, EnvironmentState State, bool HasUnsettledProvisioningOperation = false);

/// <summary>
/// Pure policy, NOT a reservation service. Snapshot must include all rows for the owner across pools
/// and all rows of the target pool across owners, exactly once per reservation. No in-memory locking
/// can prevent write skew: the database must serialize competing quota checks/reservations (for example
/// explicit locked quota rows or serializable isolation), then persist reservation, unique resource names,
/// request/idempotency key and outbox in the same transaction. Rowversion alone is insufficient.
/// </summary>
public static class EnvironmentQuotaPolicy
{
    public static QuotaDecision CanReserve(EnvironmentReservation proposed, TimeSpan requestedTtl,
        EnvironmentQuotaLimits limits, PoolBudget pool, IReadOnlyCollection<EnvironmentQuotaEntry> snapshot)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!pool.Enabled || pool.CapacityBytes <= 0 || pool.MaxEnvironmentBytes <= 0 ||
            pool.MaxConcurrentProvisioning <= 0)
            return QuotaDecision.Reject("PoolDisabledOrUnbudgeted");
        if (limits.MaxActiveEnvironments <= 0 || limits.MaxConcurrentProvisioning <= 0 ||
            limits.MaxStorageBytes <= 0 || limits.MaxTtl <= TimeSpan.Zero)
            return QuotaDecision.Reject("InvalidQuotaConfiguration");
        if (requestedTtl <= TimeSpan.Zero || requestedTtl > limits.MaxTtl)
            return QuotaDecision.Reject("TtlExceeded");
        Guard.Require(snapshot.Select(x => x.ReservationId).Distinct().Count() == snapshot.Count &&
            snapshot.All(x => x.ReservationId != Guid.Empty && x.OwnerId != Guid.Empty &&
                x.PoolId != Guid.Empty && x.ReservedBytes > 0 && Enum.IsDefined(x.State) &&
                !(x.State == EnvironmentState.Deleted && x.HasUnsettledProvisioningOperation)),
            "InvalidSnapshot", "Quota snapshot must have unique, valid reservation rows.");
        Guard.Require(snapshot.All(x => x.ReservationId != proposed.Id),
            "ReservationAlreadyPresent", "Existing reservations are not new admission requests.");
        var retained = snapshot.Where(x => x.State != EnvironmentState.Deleted).ToArray();
        var owner = retained.Where(x => x.OwnerId == proposed.OwnerId).ToArray();
        var targetPool = retained.Where(x => x.PoolId == proposed.PoolId).ToArray();
        if (owner.Length >= limits.MaxActiveEnvironments) return QuotaDecision.Reject("EnvironmentCountExceeded");
        if (owner.Count(x => IsProvisioning(x)) >= limits.MaxConcurrentProvisioning)
            return QuotaDecision.Reject("OwnerProvisioningExceeded");
        if (targetPool.Count(x => IsProvisioning(x)) >= pool.MaxConcurrentProvisioning)
            return QuotaDecision.Reject("PoolProvisioningExceeded");
        // Decimal avoids overflow even if a damaged snapshot contains very large allocations.
        if (owner.Sum(x => (decimal)x.ReservedBytes) + proposed.ReservedBytes > limits.MaxStorageBytes)
            return QuotaDecision.Reject("OwnerStorageExceeded");
        if (proposed.ReservedBytes > pool.MaxEnvironmentBytes)
            return QuotaDecision.Reject("EnvironmentSizeExceeded");
        if (targetPool.Sum(x => (decimal)x.ReservedBytes) + proposed.ReservedBytes > pool.CapacityBytes)
            return QuotaDecision.Reject("PoolCapacityExceeded");
        return QuotaDecision.Permit;
    }

    // Only a settled durable operation journal can clear this flag, not a failed job/expired lease.
    private static bool IsProvisioning(EnvironmentQuotaEntry entry) => entry.HasUnsettledProvisioningOperation ||
        entry.State is EnvironmentState.Requested or EnvironmentState.Provisioning or EnvironmentState.Preparing;
}
