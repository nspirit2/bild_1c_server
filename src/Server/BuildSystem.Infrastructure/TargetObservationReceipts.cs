using System.Data;
using BuildSystem.Application;
using BuildSystem.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Stores historical observation evidence, without changing any intent, lease, lifecycle or hold.</summary>
public sealed class SqlTargetObservationReceipts : ITargetObservationReceipts
{
    private readonly BuildSystemDbContext db;
    private readonly bool enabled;
    private readonly Dictionary<Guid, TargetObservationEndpointMapping> endpointMappings;

    public SqlTargetObservationReceipts(BuildSystemDbContext db, TargetObservationOptions options)
    {
        this.db = db;
        enabled = options.Enabled;
        endpointMappings = options.EndpointMappings is { Count: <= 1024 }
            ? new(options.EndpointMappings) : [];
    }

    public Task<TargetObservationReceipt> ReportAsync(string credential, ReportTargetObservation report, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credential) || credential.Length > 256) throw Unauthorized();
        if (!enabled) throw Reject(409, "TargetObservationDisabled");
        if (report is null || report.ReceiptId == Guid.Empty || !ValidAssignment(report.Assignment) ||
            report.RacObservationId == Guid.Empty || report.SqlObservationId == Guid.Empty ||
            report.RacObservationId == report.SqlObservationId || (int)report.Outcome is < 0 or > 3 ||
            report.ObservedAt == default)
            throw Reject(400, "InvalidTargetObservationReceipt");
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await TargetObservationAccess.LockAsync(db, "BuildSystem:agent-control", ct);
                var agent = await TargetObservationAccess.AuthenticateAsync(db, credential, ct);
                var assigned = await db.TargetObservations.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.ObservationRequestId == report.Assignment.ObservationRequestId, ct);
                if (assigned is not null && assigned.AgentId != agent.Id) throw Unauthorized();
                if (assigned is null || TargetObservationAccess.Assignment(assigned) != report.Assignment ||
                    assigned.Executable || !assigned.ScheduledJobsMustRemainBlocked ||
                    assigned.RequestedBy == Guid.Empty || assigned.RequestedAt == default)
                    throw Mismatch();
                await TargetObservationAccess.ValidateAsync(db, agent, assigned, endpointMappings,
                    "TargetObservationReceiptMismatch", ct);
                // Authentication and current bindings precede even an exact lost-ACK replay.
                // Cross-role comparisons also prevent swapping old RAC/SQL evidence into another assignment.
                var matches = await db.TargetObservationReceipts.Where(x => x.ReceiptId == report.ReceiptId ||
                    x.ObservationRequestId == assigned.ObservationRequestId ||
                    x.RacObservationId == report.RacObservationId || x.SqlObservationId == report.SqlObservationId ||
                    x.RacObservationId == report.SqlObservationId || x.SqlObservationId == report.RacObservationId)
                    .Take(2).ToListAsync(ct);
                if (matches.Count > 1) throw Mismatch();
                var prior = matches.SingleOrDefault();
                if (prior is not null)
                {
                    if (prior.ReceiptId != report.ReceiptId || prior.ObservationRequestId != assigned.ObservationRequestId ||
                        prior.RacObservationId != report.RacObservationId || prior.SqlObservationId != report.SqlObservationId ||
                        prior.Outcome != (int)report.Outcome || prior.ObservedAt != report.ObservedAt ||
                        prior.AcceptedAt == default || prior.Executable || !prior.ScheduledJobsMustRemainBlocked)
                        throw Mismatch();
                    await tx.CommitAsync(ct);
                    return Receipt(prior);
                }
                // ObservedAt is an agent historical label, deliberately not compared with now for freshness.
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
                var row = new TargetObservationReceiptRecord
                {
                    ReceiptId = report.ReceiptId, ObservationRequestId = assigned.ObservationRequestId,
                    RacObservationId = report.RacObservationId, SqlObservationId = report.SqlObservationId,
                    Outcome = (int)report.Outcome, ObservedAt = report.ObservedAt, AcceptedAt = now,
                    Executable = false, ScheduledJobsMustRemainBlocked = true
                };
                db.TargetObservationReceipts.Add(row);
                db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = agent.Id,
                    Action = "TargetObservationRecorded", TargetId = row.ReceiptId, CreatedAt = now });
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Receipt(row);
            }
            catch
            {
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private static bool ValidAssignment(TargetObservationAssignment? x) => x is not null &&
        x.ObservationRequestId != Guid.Empty && x.RestoreIntentId != Guid.Empty && x.AgentId != Guid.Empty &&
        x.OperationId != Guid.Empty && x.EnvironmentId != Guid.Empty && x.StageJobId != Guid.Empty &&
        x.FencingToken > 0 && x.ReservationId != Guid.Empty && x.TargetIncarnationId != Guid.Empty &&
        x.CanonicalClusterId != Guid.Empty && x.CanonicalSqlServerId != Guid.Empty &&
        x.RacEndpointMappingId != Guid.Empty && x.SqlEndpointMappingId != Guid.Empty;
    private static TargetObservationReceipt Receipt(TargetObservationReceiptRecord x) => new(x.ReceiptId, x.ObservationRequestId, "Recorded");
    private static RequestRejectedException Unauthorized() => Reject(401, "AgentUnauthorized");
    private static RequestRejectedException Mismatch() => Reject(409, "TargetObservationReceiptMismatch");
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
// end
