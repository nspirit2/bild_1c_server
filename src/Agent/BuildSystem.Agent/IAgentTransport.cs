using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public interface IAgentTransport
{
    Task CapabilitiesAsync(CancellationToken ct);
    Task<LeaseGrant?> PollAsync(CancellationToken ct);
    Task<LeaseStatus> HeartbeatAsync(LeaseHeartbeat heartbeat, CancellationToken ct);
    Task ReportAsync(ReportAgentEvent report, CancellationToken ct);
}
// end
