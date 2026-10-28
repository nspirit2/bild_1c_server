using System.Net;

namespace BuildSystem.Agent;

/// <summary>Explicit one-result command. No cleanup execution, claims, worker or quota changes.</summary>
public static class CleanupDeliveryCommand
{
    internal static bool IsDeliveryCommand(string[] args) => args.Length == 1 && args[0] == "deliver-cleanup-result";
    internal static bool SupportedArguments(string[] args) => args.Length == 0 ||
        (args.Length == 1 && args[0] is "import-token" or "deliver-cleanup-result");

    public static async Task<int> RunAsync(AgentJournal journal, ICleanupResultTransport transport,
        TextWriter output, CancellationToken ct = default)
    {
        try
        {
            var delivered = await new CleanupResultDelivery(journal, transport).DeliverOneAsync(ct);
            await output.WriteLineAsync(delivered ? "CleanupResultDelivered" : "CleanupResultNoPending");
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await output.WriteLineAsync("CleanupResultCancelled"); return 130; }
        catch (OperationCanceledException)
        { await output.WriteLineAsync("CleanupResultTransient"); return 3; }
        catch (CleanupResultReceiptException)
        { await output.WriteLineAsync("CleanupResultBlocked"); return 4; }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        { await output.WriteLineAsync("CleanupResultAuthenticationRejected"); return 2; }
        catch (CleanupResultConflictException)
        { await output.WriteLineAsync("CleanupResultBlocked"); return 4; }
        catch (CleanupResultInvalidException)
        { await output.WriteLineAsync("CleanupResultBlocked"); return 4; }
        catch (HttpRequestException)
        { await output.WriteLineAsync("CleanupResultTransient"); return 3; }
        catch (Exception)
        {
            // Journal failures can occur after remote acceptance. Keep the same durable ResultId;
            // never print exceptions (HTTP bodies, credentials and local paths may be sensitive).
            await output.WriteLineAsync("CleanupResultStorageOrReplayFailure"); return 5;
        }
    }
}
// end
