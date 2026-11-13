using System.Data;
using System.Globalization;
using BuildSystem.Application;
using BuildSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace BuildSystem.Infrastructure;

/// <summary>Immutable historical readback only; never grants execution or establishes ownership.</summary>
public sealed class SqlTargetRegistrationEvidenceReceipts(BuildSystemDbContext db,
    TargetRegistrationEvidenceReceiptOptions options, TargetCreationOperationOptions creation)
    : ITargetRegistrationEvidenceReceipts
{
    private const string RegistrationProfile = "rac-registration-8.3.22.2557-v1";
    private readonly SqlTargetCreationOperations ledger = new(db, creation);

    public async Task<TargetRegistrationEvidenceReceipt> ReportAsync(string agentToken,
        ReportTargetRegistrationEvidence report, CancellationToken ct)
    {
        // Historical ingestion has its own gate, independent of executable issuance switches.
        if (!options.Enabled) throw Reject(409, "TargetRegistrationEvidenceReceiptDisabled");
        if (string.IsNullOrEmpty(agentToken) || agentToken.Length > 256) throw Unauthorized();
        if (!ValidReport(report)) throw Reject(400, "InvalidTargetRegistrationEvidenceReceipt");
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                TargetRegistrationEvidenceRecord accepted;
                await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
                {
                    await ledger.Lock("BuildSystem:agent-control", ct);
                    var hash = TokenProtector.Hash(agentToken);
                    var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(
                        x => x.TokenHash == hash && x.State == "Approved", ct);
                    if (agent is null || !TokenProtector.Verify(agentToken, agent.TokenHash!)) throw Unauthorized();
                    var claim = await db.TargetCreationExecutionClaims.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.ClaimId == report.ClaimId, ct) ?? throw Mismatch();
                    if (claim.AgentId != agent.Id) throw Unauthorized();
                    var permit = await db.TargetCreationPermits.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.PermitId == claim.PermitId, ct) ?? throw Mismatch();
                    var operation = await db.TargetCreationOperations.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.OperationId == claim.OperationId, ct) ?? throw Mismatch();
                    var intent = await db.RestoreIntents.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.RestoreIntentId == operation.RestoreIntentId, ct) ?? throw Mismatch();
                    if (intent.AgentId != agent.Id) throw Unauthorized();
                    await ledger.Admission(intent, ct);
                    try { _ = SqlTargetCreationPermits.Receipt(permit, operation, intent, false); }
                    catch (RequestRejectedException) { throw Mismatch(); }
                    var now = await db.Database.SqlQuery<DateTimeOffset>(
                        $"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(ct);
                    if (new[] { claim.ClaimId, claim.PermitId, claim.OperationId, claim.AttemptId,
                            claim.AgentId, claim.CredentialId, claim.EndpointMappingId }.Any(x => x == Guid.Empty) ||
                        claim.OperationId != permit.OperationId || claim.AttemptId != permit.AttemptId ||
                        claim.CredentialId != permit.CredentialId || claim.EndpointMappingId != permit.EndpointMappingId ||
                        claim.AttemptId != operation.AttemptId || operation.AttemptExpectedRevision != 1 ||
                        !(operation.State == TargetCreationOperationState.AttemptRecorded && operation.Revision == 2 ||
                          operation.State == TargetCreationOperationState.ReconciliationRequired && operation.Revision == 3) ||
                        permit.StartedAt is not { } started || permit.StartExpectedRevision != 1 ||
                        !(permit.State == TargetCreationPermitState.StartRecorded && permit.Revision == 2 ||
                          permit.State == TargetCreationPermitState.Revoked && permit.Revision == 3) ||
                        claim.IssuedAt == default || claim.IssuedAt.Offset != TimeSpan.Zero ||
                        claim.ExpiresAt.Offset != TimeSpan.Zero || now.Offset != TimeSpan.Zero ||
                        claim.IssuedAt < permit.IssuedAt || claim.IssuedAt < started || claim.IssuedAt > now ||
                        claim.ExpiresAt != permit.StartBefore || claim.ExpiresAt <= claim.IssuedAt ||
                        claim.ExpiresAt - claim.IssuedAt > TimeSpan.FromMinutes(5) ||
                        !string.Equals(report.InfobaseName, intent.InfobaseName, StringComparison.Ordinal) ||
                        !string.Equals(report.DatabaseName, intent.SqlDbName, StringComparison.Ordinal))
                        throw Mismatch();

                    // Current credential metadata only: never select/decrypt an envelope, even on replay.
                    var credential = await db.TargetCredentials.AsNoTracking()
                        .Where(x => x.CredentialId == claim.CredentialId)
                        .Select(x => new { x.CredentialId, x.CanonicalClusterId, x.CanonicalSqlServerId,
                            x.EndpointMappingId, x.State, x.Revision, x.CreatedAt, x.RevokedAt })
                        .SingleOrDefaultAsync(ct);
                    if (credential is null || credential.CredentialId != claim.CredentialId ||
                        credential.CanonicalClusterId != intent.CanonicalClusterId ||
                        credential.CanonicalSqlServerId != intent.CanonicalSqlServerId ||
                        credential.EndpointMappingId != claim.EndpointMappingId ||
                        credential.State != TargetCredentialState.Stored || credential.Revision != 1 ||
                        credential.RevokedAt is not null || credential.CreatedAt == default ||
                        credential.CreatedAt.Offset != TimeSpan.Zero || credential.CreatedAt > claim.IssuedAt ||
                        credential.CreatedAt > now)
                        throw Mismatch();

                    // Reported endpoint mapping IDs are evidence labels, not trusted creation pins.
                    var candidate = new TargetRegistrationEvidenceRecord
                    {
                        ClaimId = claim.ClaimId, OperationId = claim.OperationId, AgentId = agent.Id,
                        AttemptId = claim.AttemptId, PermitId = claim.PermitId, CredentialId = claim.CredentialId,
                        CreationEndpointMappingId = claim.EndpointMappingId, RestoreIntentId = intent.RestoreIntentId,
                        FencingToken = intent.FencingToken, TargetIncarnationId = intent.TargetIncarnation,
                        ReservationId = intent.ReservationId, CanonicalClusterId = intent.CanonicalClusterId,
                        CanonicalSqlServerId = intent.CanonicalSqlServerId, ReportedInfobaseId = report.ReportedInfobaseId,
                        RacObservationId = report.RacObservationId, SqlObservationId = report.SqlObservationId,
                        RacEndpointMappingId = report.RacEndpointMappingId, SqlEndpointMappingId = report.SqlEndpointMappingId,
                        RegistrationProfileId = report.RegistrationProfileId, InfobaseName = report.InfobaseName,
                        SqlServer = report.SqlServer, DatabaseName = report.DatabaseName, SqlDatabaseId = report.SqlDatabaseId,
                        SqlCreateDate = report.SqlCreateDate, SqlState = report.SqlState, AcceptedAt = now
                    };
                    // Current auth, admission and metadata checks precede every lost-ACK lookup.
                    var matches = await db.TargetRegistrationEvidenceReceipts.AsNoTracking().Where(x =>
                        x.ClaimId == claim.ClaimId || x.OperationId == claim.OperationId || x.AttemptId == claim.AttemptId ||
                        x.RacObservationId == report.RacObservationId || x.SqlObservationId == report.SqlObservationId ||
                        x.RacObservationId == report.SqlObservationId || x.SqlObservationId == report.RacObservationId)
                        .Take(2).ToListAsync(ct);
                    if (matches.Count > 1) throw Mismatch();
                    var prior = matches.SingleOrDefault();
                    if (prior is not null)
                    {
                        if (!SamePayload(prior, candidate) || prior.AcceptedAt == default ||
                            prior.AcceptedAt.Offset != TimeSpan.Zero || prior.AcceptedAt > now) throw Mismatch();
                        accepted = prior;
                    }
                    else
                    {
                        accepted = candidate;
                        db.TargetRegistrationEvidenceReceipts.Add(candidate);
                        db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), ActorId = agent.Id,
                            Action = "TargetRegistrationEvidenceRecorded", TargetId = claim.ClaimId, CreatedAt = now });
                        await db.SaveChangesAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                }
                // Commit AND disposal must complete before constructing even this inert response.
                return new TargetRegistrationEvidenceReceipt(accepted.ClaimId, accepted.OperationId, "Recorded", accepted.AcceptedAt);
            });
        }
        catch (RequestRejectedException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            try { db.ChangeTracker.Clear(); } catch { /* Cleanup cannot expose a provider/disposal failure. */ }
            throw Reject(503, "TargetRegistrationEvidenceReceiptUnavailable");
        }
    }

    private static bool ValidReport(ReportTargetRegistrationEvidence? x) => x is not null &&
        x.ClaimId != Guid.Empty && x.ReportedInfobaseId != Guid.Empty &&
        x.RacObservationId != Guid.Empty && x.SqlObservationId != Guid.Empty && x.RacObservationId != x.SqlObservationId &&
        x.RacEndpointMappingId != Guid.Empty && x.SqlEndpointMappingId != Guid.Empty &&
        string.Equals(x.RegistrationProfileId, RegistrationProfile, StringComparison.Ordinal) &&
        ValidName(x.InfobaseName) && ValidName(x.DatabaseName) &&
        x.SqlServer is { Length: >= 1 and <= 255 } && x.SqlServer[0] != '-' &&
        x.SqlServer.All(c => c is >= '!' and <= '~') && x.SqlDatabaseId > 0 &&
        string.Equals(x.SqlState, "ONLINE", StringComparison.Ordinal) &&
        ValidCanonicalSqlDate(x.SqlCreateDate);

    private static bool ValidCanonicalSqlDate(string? value)
    {
        // Validate the provider's wall-clock core without converting Local Kind into this server's zone.
        if (value is null || value.Length is not (27 or 28 or 33)) return false;
        const string coreFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
        var core = value[..27];
        if (!DateTime.TryParseExact(core, coreFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            date == default || !string.Equals(date.ToString(coreFormat, CultureInfo.InvariantCulture), core, StringComparison.Ordinal))
            return false;
        if (value.Length == 27) return true; // Unspecified Kind.
        if (value.Length == 28) return value[27] == 'Z'; // UTC Kind.
        // Offset-bearing Local Kind: DateTimeOffset preserves the original offset across host time zones.
        return DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var offsetDate) &&
            string.Equals(offsetDate.ToString("O", CultureInfo.InvariantCulture), value, StringComparison.Ordinal);
    }

    private static bool ValidName(string? value) => value is { Length: >= 1 and <= 128 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private static bool SamePayload(TargetRegistrationEvidenceRecord a, TargetRegistrationEvidenceRecord b) =>
        a.ClaimId == b.ClaimId && a.OperationId == b.OperationId && a.AgentId == b.AgentId &&
        a.AttemptId == b.AttemptId && a.PermitId == b.PermitId && a.CredentialId == b.CredentialId &&
        a.CreationEndpointMappingId == b.CreationEndpointMappingId && a.RestoreIntentId == b.RestoreIntentId &&
        a.FencingToken == b.FencingToken && a.TargetIncarnationId == b.TargetIncarnationId &&
        a.ReservationId == b.ReservationId && a.CanonicalClusterId == b.CanonicalClusterId &&
        a.CanonicalSqlServerId == b.CanonicalSqlServerId && a.ReportedInfobaseId == b.ReportedInfobaseId &&
        a.RacObservationId == b.RacObservationId && a.SqlObservationId == b.SqlObservationId &&
        a.RacEndpointMappingId == b.RacEndpointMappingId && a.SqlEndpointMappingId == b.SqlEndpointMappingId &&
        string.Equals(a.RegistrationProfileId, b.RegistrationProfileId, StringComparison.Ordinal) &&
        string.Equals(a.InfobaseName, b.InfobaseName, StringComparison.Ordinal) &&
        string.Equals(a.SqlServer, b.SqlServer, StringComparison.Ordinal) &&
        string.Equals(a.DatabaseName, b.DatabaseName, StringComparison.Ordinal) && a.SqlDatabaseId == b.SqlDatabaseId &&
        string.Equals(a.SqlCreateDate, b.SqlCreateDate, StringComparison.Ordinal) &&
        string.Equals(a.SqlState, b.SqlState, StringComparison.Ordinal);

    private static RequestRejectedException Unauthorized() => Reject(401, "AgentUnauthorized");
    private static RequestRejectedException Mismatch() => Reject(409, "TargetRegistrationEvidenceReceiptMismatch");
    private static RequestRejectedException Reject(int status, string code) => new(status, code);
}
