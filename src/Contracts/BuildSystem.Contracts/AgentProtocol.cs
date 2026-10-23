namespace BuildSystem.Contracts;

public sealed record CreateEnrollment(Guid ExpectedAgentId, string ExpectedName, Guid PoolId);
public sealed record EnrollmentTicket(Guid EnrollmentId, string Token, DateTimeOffset ExpiresAt);
public sealed record RegisterAgent(Guid AgentId, Guid EnrollmentId, string Token, string Name);
public sealed record RegistrationReceipt(Guid AgentId, string State);
public sealed record ApproveAgent(Guid AgentId);
// Agent token is delivered out of band by the administrator and never via cookie-readable agent polling.
public sealed record AgentCredential(Guid AgentId, string Token);
public sealed record AgentCapabilities(string[] SupportedJobKinds);
public sealed record LeaseGrant(Guid JobId, long FencingToken, DateTimeOffset ExpiresAt, string Kind, string SnapshotJson);
public sealed record LeaseHeartbeat(Guid JobId, long FencingToken);
public sealed record LeaseStatus(DateTimeOffset ExpiresAt);
public sealed record ReportAgentEvent(Guid EventId, Guid JobId, long FencingToken, string Code);
public sealed record EventReceipt(Guid EventId);
public sealed record AgentView(Guid Id, string Name, string State, Guid PoolId, DateTimeOffset? LastHeartbeat);
// End of protocol.
