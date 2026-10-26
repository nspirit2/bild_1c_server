using System.Net;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public enum StageCompletionDeliveryResult { Empty, Acknowledged, RetryLater, Blocked }

/// <summary>Serialized durable completion delivery. Pending replay is allowed past expiry because
/// a lost response may already have completed the server lease. Only the server receipt settles locally.</summary>
public sealed class StageCompletionDelivery(AgentJournal journal, IStageCompletionTransport transport)
{
    public async Task<StageCompletionDeliveryResult> FlushAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var item = journal.PendingCompletions().FirstOrDefault();
        if (item is null) return StageCompletionDeliveryResult.Empty;
        if (item.State == "Blocked") return StageCompletionDeliveryResult.Blocked;
        var report = item.Report;
        StageCompletionReceipt receipt;
        try
        {
            receipt = await transport.CompleteStageAsync(report, ct);
            ct.ThrowIfCancellationRequested();
            if (receipt is null || receipt.JobId != report.JobId || receipt.FencingToken != report.FencingToken ||
                receipt.ArtifactId != report.ArtifactId || receipt.State != "StageCompleted") return Block("ReceiptMismatch");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { return Block("AuthRejected"); }
        catch (HttpRequestException error) when (error.StatusCode is { } status && (int)status >= 400 && (int)status < 500 && (int)status != 429)
        { return Block("LeaseRejected"); }
        catch (HttpRequestException) { return StageCompletionDeliveryResult.RetryLater; }
        catch (IOException) { return StageCompletionDeliveryResult.RetryLater; }
        catch (OperationCanceledException) { return StageCompletionDeliveryResult.RetryLater; }
        catch (InvalidOperationException) { return Block("ReceiptMismatch"); }
        // Journal failures must propagate for reconciliation, not be relabelled as server mismatch.
        journal.AcknowledgeCompletion(receipt);
        return StageCompletionDeliveryResult.Acknowledged;

        StageCompletionDeliveryResult Block(string code)
        {
            journal.BlockCompletion(report.JobId, report.FencingToken, code);
            return StageCompletionDeliveryResult.Blocked;
        }
    }
}
// end
