using BuildSystem.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildSystem.Agent;

public sealed class ProbeWorker(IAgentTransport transport, AgentJournal journal, ILogger<ProbeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WorkOnce(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // No token, URLs, job snapshots or exception text in logs: HttpClient errors can contain URLs.
                logger.LogWarning("Agent control-plane request failed: {ErrorType}", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    public async Task WorkOnce(CancellationToken ct)
    {
        // Capability advertisement gives NO authorization beyond the admin-approved pool.
        await transport.CapabilitiesAsync(ct);
        foreach (var pending in journal.Pending())
        {
            // Do not replay a stale/expired event. A fenced-out lease is an operator reconciliation case.
            var lease = journal.Unsettled().FirstOrDefault(x => x.JobId == pending.JobId && x.FencingToken == pending.FencingToken);
            if (lease is null || lease.ExpiresAt <= DateTimeOffset.UtcNow) return;
            await transport.ReportAsync(pending, ct);
            journal.Acknowledge(pending.EventId);
        }
        var existing = journal.Unsettled();
        if (existing.Count > 0)
        {
            // On restart never repeat even a probe. Report unknown, keep journal until operator reconciliation.
            var lease = existing[0];
            if (lease.ExpiresAt > DateTimeOffset.UtcNow && !journal.HasEvent(lease.JobId, lease.FencingToken, "LeaseOutcomeUnknown"))
            {
                var report = new ReportAgentEvent(Guid.NewGuid(), lease.JobId, lease.FencingToken, "LeaseOutcomeUnknown");
                journal.Enqueue(report);
                await transport.ReportAsync(report, ct);
                journal.Acknowledge(report.EventId);
            }
            return;
        }
        var grant = await transport.PollAsync(ct);
        if (grant is null) return;
        // Only a probe is implemented; never deserialize or execute untrusted snapshot instructions.
        if (journal.IsSettled(grant.JobId, grant.FencingToken)) return;
        journal.StoreGrant(grant);
        if (grant.ExpiresAt <= DateTimeOffset.UtcNow) return;
        var code = grant.Kind == "ControlPlaneProbe" && grant.SnapshotJson == "{}"
            ? "ProbeCompleted" : "WorkerUnsupported";
        // HTTP heartbeat is intentionally before event dispatch; rejected lease stops all new actions.
        var status = await transport.HeartbeatAsync(new LeaseHeartbeat(grant.JobId, grant.FencingToken), ct);
        journal.UpdateExpiry(grant.JobId, grant.FencingToken, status.ExpiresAt);
        if (status.ExpiresAt <= DateTimeOffset.UtcNow) return;
        var eventData = new ReportAgentEvent(Guid.NewGuid(), grant.JobId, grant.FencingToken, code);
        journal.Enqueue(eventData);
        await transport.ReportAsync(eventData, ct);
        journal.Acknowledge(eventData.EventId);
    }
}
// End.
