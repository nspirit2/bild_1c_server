using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildSystem.Agent;

/// <summary>Opt-in alternate service loop. Never run alongside ProbeWorker on the same journal.</summary>
public sealed class StageHostedWorker(StageWorker worker, ILogger<StageHostedWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await worker.WorkOnce(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Stage iteration failed; durable local state retained for reconciliation."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
// end
