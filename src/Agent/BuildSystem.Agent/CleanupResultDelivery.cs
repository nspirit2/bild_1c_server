using System.Net;
namespace BuildSystem.Agent;

/// <summary>Explicit one-result delivery only. Caller serializes all journal access; no claim,
/// local deletion, staging release, retry loop or hosted-worker registration occurs here.</summary>
public sealed class CleanupResultDelivery(AgentJournal journal, ICleanupResultTransport transport)
{
    public async Task<bool> DeliverOneAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = journal.FirstPendingCleanupResult();
        if (result is null) return false;
        try
        {
            var receipt = await transport.ReportCleanupResultAsync(result, ct);
            // Validate even an alternate transport implementation; never trust its typed DTO alone.
            AgentTransport.ValidateCleanupReceipt(result, receipt);
            journal.AckCleanupResult(receipt);
            return true;
        }
        catch (CleanupResultReceiptException)
        {
            journal.BlockCleanupResult(result.ResultId);
            throw;
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            journal.BlockCleanupResult(result.ResultId);
            throw;
        }
        // Auth rotation, rate limits, network/storage failure and cancellation remain Pending.
    }
}
public sealed partial class AgentJournal
{
    // One row per explicit delivery call; do not materialize an unbounded outbox batch.
    internal BuildSystem.Contracts.ReportStageCleanup? FirstPendingCleanupResult()
    {
        using var command = Command("SELECT result,authorization,retirement,job,artifact,fence FROM cleanup_results WHERE state='Pending' ORDER BY rowid LIMIT 1");
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), Guid.Parse(reader.GetString(4)), reader.GetInt64(5)) : null;
    }
}
// end
