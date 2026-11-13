using System.Data;
using System.Globalization;
using BuildSystem.Application;
using BuildSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Read-only stored history; never reconstructs authority or consults current credentials.</summary>
public sealed class SqlTargetRegistrationEvidenceReads(BuildSystemDbContext db,
    TargetCreationOperationOptions options) : ITargetRegistrationEvidenceReads
{
    public async Task<TargetRegistrationEvidenceView> GetAsync(Actor actor, Guid operationId, CancellationToken ct)
    {
        if (!options.Enabled) throw Reject(409, "TargetCreationDisabled");
        if (operationId == Guid.Empty) throw Reject(400, "InvalidTargetCreation");
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var ledger = new SqlTargetCreationOperations(db, options);
                await ledger.Lock("BuildSystem:agent-control", ct);
                // Claims alone are insufficient: verify the current database administrator role first.
                await ledger.Admin(actor, ct);
                var operation = await db.TargetCreationOperations.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OperationId == operationId, ct)
                    ?? throw Reject(404, "TargetCreationNotFound");
                var intent = await db.RestoreIntents.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.RestoreIntentId == operation.RestoreIntentId, ct)
                    ?? throw Mismatch();
                _ = SqlTargetCreationOperations.Receipt(operation, intent, false);
                var row = await db.TargetRegistrationEvidenceReceipts.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OperationId == operationId, ct);
                TargetRegistrationEvidenceHistory? history = null;
                if (row is not null)
                {
                    var claim = await db.TargetCreationExecutionClaims.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.ClaimId == row.ClaimId, ct) ?? throw Mismatch();
                    ValidateHistory(row);
                    // Only immutable stored roots are compared. No live admission, agent, credential,
                    // archive, observation, permit state or lease check may erase historical evidence.
                    if (row.OperationId != operation.OperationId || row.RestoreIntentId != operation.RestoreIntentId ||
                        row.AttemptId != operation.AttemptId || row.ClaimId != claim.ClaimId ||
                        row.OperationId != claim.OperationId || row.AttemptId != claim.AttemptId ||
                        row.AgentId != claim.AgentId || row.PermitId != claim.PermitId ||
                        row.CredentialId != claim.CredentialId || row.CreationEndpointMappingId != claim.EndpointMappingId ||
                        row.AgentId != intent.AgentId || row.FencingToken != intent.FencingToken ||
                        row.TargetIncarnationId != intent.TargetIncarnation || row.ReservationId != intent.ReservationId ||
                        row.CanonicalClusterId != intent.CanonicalClusterId || row.CanonicalSqlServerId != intent.CanonicalSqlServerId ||
                        !string.Equals(row.InfobaseName, intent.InfobaseName, StringComparison.Ordinal) ||
                        !string.Equals(row.DatabaseName, intent.SqlDbName, StringComparison.Ordinal) ||
                        claim.IssuedAt == default || claim.IssuedAt.Offset != TimeSpan.Zero ||
                        claim.ExpiresAt.Offset != TimeSpan.Zero || claim.ExpiresAt <= claim.IssuedAt ||
                        claim.ExpiresAt - claim.IssuedAt > TimeSpan.FromMinutes(5) || row.AcceptedAt < claim.IssuedAt)
                        throw Mismatch();
                    history = new(row.ClaimId, row.OperationId, row.AgentId, row.AttemptId, row.PermitId,
                        row.CredentialId, row.CreationEndpointMappingId, row.RestoreIntentId, row.FencingToken,
                        row.TargetIncarnationId, row.ReservationId, row.CanonicalClusterId, row.CanonicalSqlServerId,
                        row.ReportedInfobaseId, row.RacObservationId, row.SqlObservationId, row.RacEndpointMappingId,
                        row.SqlEndpointMappingId, row.RegistrationProfileId, row.InfobaseName, row.SqlServer,
                        row.DatabaseName, row.SqlDatabaseId, row.SqlCreateDate, row.SqlState, row.AcceptedAt);
                }
                // No writes, audit append, credential access, target I/O or lifecycle transitions.
                await tx.CommitAsync(ct);
                return new TargetRegistrationEvidenceView(operationId, history is not null, history);
            });
        }
        catch (RequestRejectedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw Reject(503, "TargetRegistrationEvidenceHistoryUnavailable"); }
    }

    private static void ValidateHistory(TargetRegistrationEvidenceRecord row)
    {
        if (new[] { row.ClaimId, row.OperationId, row.AgentId, row.AttemptId, row.PermitId,
                row.CredentialId, row.CreationEndpointMappingId, row.RestoreIntentId, row.TargetIncarnationId,
                row.ReservationId, row.CanonicalClusterId, row.CanonicalSqlServerId, row.ReportedInfobaseId,
                row.RacObservationId, row.SqlObservationId, row.RacEndpointMappingId, row.SqlEndpointMappingId }
                .Any(x => x == Guid.Empty) || row.FencingToken <= 0 ||
            row.RacObservationId == row.SqlObservationId ||
            !string.Equals(row.RegistrationProfileId, "rac-registration-8.3.22.2557-v1", StringComparison.Ordinal) ||
            !ValidName(row.InfobaseName) || !ValidName(row.DatabaseName) ||
            row.SqlServer is not { Length: >= 1 and <= 255 } || row.SqlServer[0] == '-' ||
            !row.SqlServer.All(c => c is >= '!' and <= '~') || row.SqlDatabaseId <= 0 ||
            !string.Equals(row.SqlState, "ONLINE", StringComparison.Ordinal) || !ValidCanonicalSqlDate(row.SqlCreateDate) ||
            row.AcceptedAt == default || row.AcceptedAt.Offset != TimeSpan.Zero)
            throw Mismatch();
    }

    private static bool ValidName(string? value) => value is { Length: >= 1 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool ValidCanonicalSqlDate(string? value)
    {
        // Preserve the provider wall-clock core and its original Kind/offset across host time zones.
        if (value is null || value.Length is not (27 or 28 or 33)) return false;
        const string coreFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
        var core = value[..27];
        if (!DateTime.TryParseExact(core, coreFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            date == default || !string.Equals(date.ToString(coreFormat, CultureInfo.InvariantCulture), core, StringComparison.Ordinal))
            return false;
        if (value.Length == 27) return true;
        if (value.Length == 28) return value[27] == 'Z';
        return DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var offsetDate) &&
            string.Equals(offsetDate.ToString("O", CultureInfo.InvariantCulture), value, StringComparison.Ordinal);
    }

    private static RequestRejectedException Mismatch() => Reject(409, "TargetCreationMismatch");
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
