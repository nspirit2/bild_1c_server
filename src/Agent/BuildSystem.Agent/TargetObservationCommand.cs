namespace BuildSystem.Agent;

/// <summary>Explicit observation and addressed delivery only; never a worker or provisioning entry point.</summary>
public static class TargetObservationCommand
{
    public static bool TryParse(string[] args, out Guid requestId)
    {
        requestId = Guid.Empty;
        return args is { Length: 2 } && args[0] == "observe-target" &&
            Guid.TryParseExact(args[1], "D", out requestId) && requestId != Guid.Empty &&
            string.Equals(args[1], requestId.ToString("D"), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsObservationCommand(string[] args) => TryParse(args, out _);

    public static async Task<int> RunAsync(Guid requestId, TargetObservationCoordinator coordinator,
        TargetObservationOutboxDelivery delivery, TextWriter output, CancellationToken ct = default)
    {
        if (requestId == Guid.Empty)
        {
            await output.WriteLineAsync("UnsupportedAgentCommand");
            return 64;
        }
        try
        {
            await coordinator.ObserveAsync(requestId, ct).ConfigureAwait(false);
            var delivered = await delivery.DeliverRequestAsync(requestId, ct).ConfigureAwait(false);
            await output.WriteLineAsync(delivered == 1 ? "TargetObservationRecorded" : "TargetObservationHistoricalRecorded");
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await output.WriteLineAsync("TargetObservationCancelled"); return 130; }
        catch (TargetObservationCoordinatorException)
        { await output.WriteLineAsync("TargetObservationBlocked"); return 4; }
        catch (Exception)
        {
            // Delivery may fail after remote commit. Preserve durable receipt identity for retry.
            await output.WriteLineAsync("TargetObservationDeliveryOrStorageFailure");
            return 5;
        }
    }
}
// End.
