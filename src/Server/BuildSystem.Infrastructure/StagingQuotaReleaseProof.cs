using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Metadata-only evidence for excluding logical staging bytes from quota.</summary>
internal static class StagingQuotaReleaseProof
{
    internal static IQueryable<StagingAllocationReleaseRecord> Confirmed(BuildSystemDbContext db) =>
        from release in StagingReleaseAudit.Confirmed(db)
        join reservation in db.EnvironmentReservations on release.ReservationId equals reservation.Id
        join result in db.StageCleanupResults on release.ResultId equals result.ResultId
        join auth in db.StageCleanupAuthorizations on release.AuthorizationId equals auth.AuthorizationId
        join intent in db.StageRetirementIntents on auth.RetirementId equals intent.RetirementId
        join artifact in db.StagedArtifacts on auth.ArtifactId equals artifact.ArtifactId
        join job in db.EnvironmentJobs on intent.JobId equals job.Id
        join environment in db.TestEnvironments on intent.EnvironmentId equals environment.Id
        join request in db.EnvironmentRequests on environment.RequestId equals request.Id
        join lease in db.AgentLeases on job.Id equals lease.JobId
        join version in db.DiskArchiveVersions on artifact.ArchiveVersionId equals version.Id
        join archive in db.DiskArchives on version.ArchiveId equals archive.Id
        where result.AuthorizationId == auth.AuthorizationId && result.ArtifactId == artifact.ArtifactId &&
            intent.ArtifactId == artifact.ArtifactId && intent.ReservationId == reservation.Id &&
            artifact.ReservationId == reservation.Id && artifact.JobId == intent.JobId &&
            EF.Functions.Collate(intent.State, "Latin1_General_100_BIN2") == "Requested" && EF.Functions.DataLength(intent.State) == 18 &&
            EF.Functions.Collate(artifact.State, "Latin1_General_100_BIN2") == "ContentVerified" && EF.Functions.DataLength(artifact.State) == 30 &&
            artifact.SizeBytes == version.SizeBytes &&
            EF.Functions.Collate(artifact.Format, "Latin1_General_100_BIN2") == EF.Functions.Collate(version.Format, "Latin1_General_100_BIN2") &&
            (EF.Functions.Collate(artifact.Format, "Latin1_General_100_BIN2") == "dt" && EF.Functions.DataLength(artifact.Format) == 4 ||
              EF.Functions.Collate(artifact.Format, "Latin1_General_100_BIN2") == "bak" && EF.Functions.DataLength(artifact.Format) == 6) &&
             EF.Functions.DataLength(artifact.Format) == EF.Functions.DataLength(version.Format) &&
            EF.Functions.Collate(artifact.Sha256, "Latin1_General_100_CI_AS") == EF.Functions.Collate(version.Sha256, "Latin1_General_100_CI_AS") &&
            EF.Functions.DataLength(artifact.Sha256) == 128 && EF.Functions.DataLength(version.Sha256) == 128 &&
             !EF.Functions.Like(EF.Functions.Collate(artifact.Sha256, "Latin1_General_100_BIN2"), "%[^0-9a-fA-F]%") &&
             !EF.Functions.Like(EF.Functions.Collate(version.Sha256, "Latin1_General_100_BIN2"), "%[^0-9a-fA-F]%") &&
            result.AgentId == auth.AgentId && auth.AgentId == intent.AgentId && intent.AgentId == artifact.AgentId &&
            artifact.FencingToken > 0 && result.StageFencingToken == auth.StageFencingToken &&
            auth.StageFencingToken == intent.FencingToken && intent.FencingToken == artifact.FencingToken &&
            EF.Functions.Collate(lease.State, "Latin1_General_100_BIN2") == "Completed" &&
            EF.Functions.DataLength(lease.State) == 18 && lease.AgentId == artifact.AgentId &&
            lease.FencingToken == artifact.FencingToken &&
            job.EnvironmentId == environment.Id && job.RequestId == request.Id &&
            request.EnvironmentId == environment.Id && request.JobId == job.Id && request.ArchiveVersionId == version.Id &&
            environment.ReservationId == reservation.Id && environment.PoolId == reservation.PoolId &&
            request.OwnerId == environment.OwnerId && request.OwnerId == reservation.OwnerId && request.ProjectId == environment.ProjectId &&
            EF.Functions.Collate(job.Kind, "Latin1_General_100_BIN2") == "ProvisionArchive" && EF.Functions.DataLength(job.Kind) == 32 &&
            EF.Functions.Collate(job.State, "Latin1_General_100_BIN2") == "StageCompleted" && EF.Functions.DataLength(job.State) == 28 &&
            environment.HasUnsettledProvisioningOperation &&
            (EF.Functions.Collate(environment.State, "Latin1_General_100_BIN2") == "Requested" && EF.Functions.DataLength(environment.State) == 18 ||
             EF.Functions.Collate(environment.State, "Latin1_General_100_BIN2") == "Provisioning" && EF.Functions.DataLength(environment.State) == 24) &&
            (EF.Functions.Collate(request.State, "Latin1_General_100_BIN2") == "Pending" && EF.Functions.DataLength(request.State) == 14 ||
             EF.Functions.Collate(request.State, "Latin1_General_100_BIN2") == "Provisioning" && EF.Functions.DataLength(request.State) == 24) &&
            EF.Functions.Collate(reservation.State, "Latin1_General_100_BIN2") == "Held" && EF.Functions.DataLength(reservation.State) == 8 &&
            release.PoolId == reservation.PoolId && release.RootId == archive.RootId &&
            release.ReleasedBytes > 0 && release.ReleasedBytes == reservation.StagingBytes &&
            release.ReleasedBytes == artifact.SizeBytes &&
            !db.StagingAllocationReleases.Any(other => other.AuthorizationId == release.AuthorizationId &&
                other.ReleaseId != release.ReleaseId) &&
            !db.StageRetirementIntents.Any(other => other.RetirementId != intent.RetirementId &&
                (other.EnvironmentId == environment.Id || other.JobId == job.Id ||
                 other.ReservationId == reservation.Id || other.ArtifactId == artifact.ArtifactId)) &&
            !db.RestoreIntents.Any(hold => hold.EnvironmentId == intent.EnvironmentId ||
                hold.StageJobId == intent.JobId || hold.ReservationId == reservation.Id || hold.ArtifactId == artifact.ArtifactId ||
                hold.RequestId == request.Id)
        select release;
}
