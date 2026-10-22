using System.Security.Cryptography;
using System.Text;
using BuildSystem.Contracts;

namespace BuildSystem.Application;

public interface IAgentControl
{
    Task<EnrollmentTicket> CreateEnrollmentAsync(Actor actor, CreateEnrollment request, CancellationToken ct);
    Task<RegistrationReceipt> RegisterAsync(RegisterAgent request, CancellationToken ct);
    Task<AgentCredential> ApproveAsync(Actor actor, Guid agentId, CancellationToken ct);
    Task RevokeAsync(Actor actor, Guid agentId, CancellationToken ct);
    Task<AgentCredential> RotateAsync(Actor actor, Guid agentId, CancellationToken ct);
    Task<Guid?> AuthenticateAsync(string token, CancellationToken ct);
    Task SetCapabilitiesAsync(Guid agentId, AgentCapabilities capabilities, CancellationToken ct);
    Task<LeaseGrant?> PollAsync(Guid agentId, CancellationToken ct);
    Task<LeaseStatus> HeartbeatAsync(Guid agentId, LeaseHeartbeat heartbeat, CancellationToken ct);
    Task<EventReceipt> ReportAsync(Guid agentId, ReportAgentEvent report, CancellationToken ct);
    Task<IReadOnlyList<AgentView>> ListAsync(Actor actor, CancellationToken ct);
}

/// <summary>Opaque 256-bit credentials. Only the SHA-256 digest is persisted; tokens are never logged.</summary>
public static class TokenProtector
{
    public static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static bool Verify(string token, string expectedHash)
    {
        if (string.IsNullOrEmpty(token) || expectedHash is null || expectedHash.Length != 64) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(token)), Convert.FromHexString(expectedHash));
        }
        catch (FormatException) { return false; }
    }
}

// AuthenticateAsync and the subsequent agent operation MUST use the same scoped IAgentControl instance.
// It binds the authenticated credential generation to the transaction's recheck, including rotation races.
// Report replays require a still-live matching lease; expired/revoked/stale replays are conflicts, not acknowledgements.
// This control plane executes only ControlPlaneProbe. Probe success does not make an environment Ready,
// does not clear unsettled provisioning, and never releases reservations or quota.
// End of agent application contract.
