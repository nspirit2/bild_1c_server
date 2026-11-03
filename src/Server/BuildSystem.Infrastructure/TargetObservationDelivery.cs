using System.Data;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Read-only addressed delivery, never a claim, lease or execution authorization.</summary>
public sealed class SqlTargetObservationDelivery : ITargetObservationDelivery
{
    private readonly BuildSystemDbContext db;
    private readonly bool enabled;
    private readonly Dictionary<Guid, TargetObservationEndpointMapping> endpointMappings;

    public SqlTargetObservationDelivery(BuildSystemDbContext db, TargetObservationOptions options)
    {
        this.db = db;
        enabled = options.Enabled;
        endpointMappings = options.EndpointMappings is { Count: <= 1024 }
            ? new(options.EndpointMappings) : [];
    }

    public Task<TargetObservationAssignment> ReadAsync(string credential, Guid observationRequestId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credential) || credential.Length > 256)
            throw new RequestRejectedException(401, "AgentUnauthorized");
        if (!enabled) throw new RequestRejectedException(409, "TargetObservationDisabled");
        if (observationRequestId == Guid.Empty)
            throw new RequestRejectedException(400, "InvalidTargetObservationDelivery");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await TargetObservationAccess.LockAsync(db, "BuildSystem:agent-control", ct);
                var agent = await TargetObservationAccess.AuthenticateAsync(db, credential, ct);
                var assigned = await db.TargetObservations.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.ObservationRequestId == observationRequestId, ct);
                if (assigned is not null && assigned.AgentId != agent.Id)
                    throw new RequestRejectedException(401, "AgentUnauthorized");
                if (assigned is null || assigned.Executable || !assigned.ScheduledJobsMustRemainBlocked ||
                    assigned.RequestedBy == Guid.Empty || assigned.RequestedAt == default)
                    throw new RequestRejectedException(409, "TargetObservationDeliveryMismatch");
                await TargetObservationAccess.ValidateAsync(db, agent, assigned, endpointMappings,
                    "TargetObservationDeliveryMismatch", ct);
                // Receipts do not consume assignments. Historical delivery never establishes freshness.
                var assignment = TargetObservationAccess.Assignment(assigned);
                await tx.CommitAsync(ct);
                return assignment;
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }
}
// end
