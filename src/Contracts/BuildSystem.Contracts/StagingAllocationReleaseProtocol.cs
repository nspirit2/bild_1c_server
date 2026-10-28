namespace BuildSystem.Contracts;

public sealed record ReleaseStagingAllocation(Guid ExpectedAuthorizationId, Guid ExpectedReservationId);
public sealed record StagingAllocationReleaseReceipt(Guid ReleaseId, Guid ResultId, Guid ReservationId, long ReleasedBytes, DateTimeOffset ReleasedAt);
// end
