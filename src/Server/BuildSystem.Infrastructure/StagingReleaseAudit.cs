using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

internal static class StagingReleaseAudit
{
    internal const string Action = "StagingAllocationReleased";

    internal static IQueryable<StagingAllocationReleaseRecord> Confirmed(BuildSystemDbContext db) =>
        db.StagingAllocationReleases.Where(release =>
            db.AuditEvents.Count(audit => audit.TargetId == release.ReleaseId &&
                EF.Functions.Collate(audit.Action, "Latin1_General_100_CI_AS") == Action) == 1 &&
            db.AuditEvents.Any(audit => audit.TargetId == release.ReleaseId && audit.ActorId == release.ActorId &&
                audit.CreatedAt == release.ReleasedAt &&
                EF.Functions.Collate(audit.Action, "Latin1_General_100_BIN2") == Action &&
                EF.Functions.DataLength(audit.Action) == 50));
}
