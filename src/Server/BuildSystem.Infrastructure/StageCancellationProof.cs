using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Shared, composable SQL proof for read-only pre-first-lease cancellation observations.</summary>
internal static class StageCancellationProof
{
    internal const string Code = "CancelledBeforeFirstLease";

    // A receipt is necessary, but does not by itself prove that today's metadata still agrees.
    // Keep this composable so admission to a list page is decided in SQL, before TOP/keyset.
    // The persisted archive snapshot has flat ASCII property names. Require that shape to avoid
    // differences in nesting limits or Unicode case folding between SQL Server and System.Text.Json.
    // Cleanup and target execution records require one of the artifact, retirement or restore anchors by FK.
    // CASE protects OPENJSON regardless of predicate evaluation order. Pinned values are compared
    // without casting untrusted input, with JSON type and exact length checks before case normalization.
    internal static IQueryable<Guid> ProvenJobIds(BuildSystemDbContext db) => db.Database.SqlQueryRaw<Guid>("""
        SELECT j.[Id] AS [Value]
        FROM [PendingArchiveCancellations] AS c
        INNER JOIN [TestEnvironments] AS e ON e.[Id] = c.[EnvironmentId]
        INNER JOIN [EnvironmentRequests] AS q ON q.[Id] = c.[RequestId] AND q.[Id] = e.[RequestId]
        INNER JOIN [EnvironmentJobs] AS j ON j.[Id] = c.[JobId] AND j.[Id] = q.[JobId]
        INNER JOIN [EnvironmentReservations] AS r ON r.[Id] = c.[ReservationId] AND r.[Id] = e.[ReservationId]
        INNER JOIN [DiskArchiveVersions] AS v ON v.[Id] = q.[ArchiveVersionId]
        INNER JOIN [DiskArchives] AS a ON a.[Id] = v.[ArchiveId]
        CROSS APPLY (VALUES (CASE WHEN ISJSON(j.[SnapshotJson]) = 1 THEN j.[SnapshotJson] ELSE N'[]' END)) AS s([Json])
        WHERE q.[EnvironmentId] = e.[Id] AND j.[EnvironmentId] = e.[Id] AND j.[RequestId] = q.[Id]
          AND q.[OwnerId] = e.[OwnerId] AND q.[OwnerId] = r.[OwnerId] AND q.[ProjectId] = e.[ProjectId]
          AND c.[PoolId] = e.[PoolId] AND r.[PoolId] = e.[PoolId] AND c.[RootId] = a.[RootId]
          AND e.[HasUnsettledProvisioningOperation] = 0 AND e.[ExpiresAt] IS NULL
          AND r.[StagingBytes] > 0 AND r.[ReservedBytes] > 0
          AND v.[SizeBytes] = r.[StagingBytes] AND v.[EstimatedDbBytes] = r.[ReservedBytes]
          AND v.[Format] COLLATE Latin1_General_100_BIN2 IN (N'dt', N'bak')
          AND DATALENGTH(v.[Format]) = DATALENGTH(RTRIM(v.[Format]))
          AND DATALENGTH(v.[Sha256]) = 128 AND v.[Sha256] COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^0-9a-fA-F]%'
          AND c.[ActorId] <> '00000000-0000-0000-0000-000000000000'
          AND DATEPART(TZOFFSET, c.[CancelledAt]) = 0 AND c.[CancelledAt] > '0001-01-01T00:00:00+00:00'
          AND j.[Kind] COLLATE Latin1_General_100_BIN2 = N'ProvisionArchive' AND DATALENGTH(j.[Kind]) = DATALENGTH(N'ProvisionArchive')
          AND j.[State] COLLATE Latin1_General_100_BIN2 = N'Cancelled' AND DATALENGTH(j.[State]) = DATALENGTH(N'Cancelled')
          AND q.[State] COLLATE Latin1_General_100_BIN2 = N'Cancelled' AND DATALENGTH(q.[State]) = DATALENGTH(N'Cancelled')
          AND e.[State] COLLATE Latin1_General_100_BIN2 = N'Cancelled' AND DATALENGTH(e.[State]) = DATALENGTH(N'Cancelled')
          AND r.[State] COLLATE Latin1_General_100_BIN2 = N'Released' AND DATALENGTH(r.[State]) = DATALENGTH(N'Released')
          AND NOT EXISTS (SELECT 1 FROM [PendingArchiveCancellations] AS other
            WHERE other.[EnvironmentId] <> e.[Id] AND (other.[RequestId] = q.[Id] OR other.[JobId] = j.[Id] OR other.[ReservationId] = r.[Id]))
          AND NOT EXISTS (SELECT 1 FROM [EnvironmentRequests] AS other
            WHERE other.[Id] <> q.[Id] AND (other.[JobId] = j.[Id] OR other.[EnvironmentId] = e.[Id]))
          AND NOT EXISTS (SELECT 1 FROM [EnvironmentJobs] AS other WHERE other.[Id] <> j.[Id] AND other.[EnvironmentId] = e.[Id])
          AND 1 = (SELECT COUNT_BIG(*) FROM [AuditEvents] AS au
            WHERE au.[TargetId] = e.[Id] AND au.[Action] = N'PendingArchiveCancelled')
          AND EXISTS (SELECT 1 FROM [AuditEvents] AS au WHERE au.[TargetId] = e.[Id]
            AND au.[Action] COLLATE Latin1_General_100_BIN2 = N'PendingArchiveCancelled'
            AND DATALENGTH(au.[Action]) = DATALENGTH(N'PendingArchiveCancelled')
            AND au.[ActorId] = c.[ActorId] AND au.[CreatedAt] = c.[CancelledAt]
            AND DATEPART(TZOFFSET, au.[CreatedAt]) = 0)
          AND NOT EXISTS (SELECT 1 FROM [AgentLeases] AS l WHERE l.[JobId] = j.[Id])
          AND NOT EXISTS (SELECT 1 FROM [AgentEvents] AS ev WHERE ev.[JobId] = j.[Id])
          AND NOT EXISTS (SELECT 1 FROM [AuditEvents] AS au WHERE au.[TargetId] = j.[Id] AND au.[Action] = N'ArchiveStageLeased')
          AND NOT EXISTS (SELECT 1 FROM [StagedArtifacts] AS ar WHERE ar.[JobId] = j.[Id] OR ar.[ReservationId] = r.[Id])
          AND NOT EXISTS (SELECT 1 FROM [StageRetirementIntents] AS i
            WHERE i.[EnvironmentId] = e.[Id] OR i.[JobId] = j.[Id] OR i.[ReservationId] = r.[Id])
          AND NOT EXISTS (SELECT 1 FROM [StagingAllocationReleases] AS rel WHERE rel.[ReservationId] = r.[Id])
          AND NOT EXISTS (SELECT 1 FROM [RestoreIntents] AS i
            WHERE i.[EnvironmentId] = e.[Id] OR i.[RequestId] = q.[Id] OR i.[StageJobId] = j.[Id] OR i.[ReservationId] = r.[Id])
          AND NOT EXISTS (SELECT 1 FROM [TargetObservations] AS o
            WHERE o.[EnvironmentId] = e.[Id] OR o.[StageJobId] = j.[Id] OR o.[ReservationId] = r.[Id])
          AND NOT EXISTS (SELECT 1 FROM OPENJSON(s.[Json]) AS p
            WHERE p.[type] IN (4, 5) OR DATALENGTH(p.[key]) > 256
              OR p.[key] COLLATE Latin1_General_100_BIN2 LIKE N'%[^a-zA-Z0-9_]%' OR DATALENGTH(p.[key]) = 0)
          AND NOT EXISTS (SELECT 1 FROM OPENJSON(s.[Json]) AS p
            GROUP BY UPPER(p.[key] COLLATE Latin1_General_100_BIN2), DATALENGTH(p.[key]) HAVING COUNT_BIG(*) > 1)
          AND 16 = (SELECT COUNT_BIG(*) FROM OPENJSON(s.[Json]) AS p
            CROSS APPLY (VALUES
                (N'reservationId', CONVERT(nvarchar(36), r.[Id]), 1),
                (N'poolId', CONVERT(nvarchar(36), r.[PoolId]), 1),
                (N'sourceRootId', CONVERT(nvarchar(36), a.[RootId]), 1),
                (N'sourceArchiveId', CONVERT(nvarchar(36), a.[Id]), 1),
                (N'archiveVersionId', CONVERT(nvarchar(36), v.[Id]), 1),
                (N'stagingBytes', CONVERT(nvarchar(36), r.[StagingBytes]), 2),
                (N'reservedBytes', CONVERT(nvarchar(36), r.[ReservedBytes]), 2),
                (N'SizeBytes', CONVERT(nvarchar(36), v.[SizeBytes]), 2),
                (N'Format', v.[Format], 1),
                (N'Sha256', v.[Sha256], 1),
                 (N'admissionMode', N'archive', 1),
                 (N'stageOnly', N'false', 3),
                 (N'executable', N'false', 3),
                 (N'scheduledJobsMustRemainBlocked', N'true', 3),
                 (N'automaticDeletionEnabled', N'false', 3),
                 (N'automaticDeletionGraceDays', NULL, 0)
            ) AS pin([Key], [Value], [Type])
            WHERE p.[key] COLLATE Latin1_General_100_BIN2 = pin.[Key] AND DATALENGTH(p.[key]) = DATALENGTH(pin.[Key])
              AND p.[type] = pin.[Type] AND ((pin.[Type] = 0 AND p.[value] IS NULL) OR
                 (DATALENGTH(p.[value]) = DATALENGTH(pin.[Value]) AND
                  LOWER(p.[value] COLLATE Latin1_General_100_BIN2) = LOWER(pin.[Value] COLLATE Latin1_General_100_BIN2)
                  AND (pin.[Key] COLLATE Latin1_General_100_BIN2 NOT IN (N'admissionMode', N'Format', N'Sha256')
                        OR p.[value] COLLATE Latin1_General_100_BIN2 = pin.[Value]))))
        """);
}
