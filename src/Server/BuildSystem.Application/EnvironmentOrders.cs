using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BuildSystem.Application;

public sealed record Actor(Guid UserId, bool IsAdministrator);
public sealed record ArchiveSource(Guid ArchiveVersionId);
public sealed record CreateEnvironmentRequest(Guid ProjectId, string DisplayName, string Mode, ArchiveSource Source, int TtlDays);
public sealed record OrderReceipt(Guid RequestId, Guid EnvironmentId, Guid JobId);
public sealed record RequestView(Guid Id, Guid EnvironmentId, Guid JobId, string State, string DisplayName, Guid ProjectId, DateTimeOffset CreatedAt);
public sealed record EnvironmentView(Guid Id, Guid ProjectId, string DisplayName, string State, DateTimeOffset? ExpiresAt);

public interface IEnvironmentOrders
{
    Task<OrderReceipt> CreateAsync(Actor actor, string idempotencyKey, CreateEnvironmentRequest request, CancellationToken ct);
    Task<RequestView?> GetRequestAsync(Actor actor, Guid id, CancellationToken ct);
    Task<IReadOnlyList<EnvironmentView>> ListEnvironmentsAsync(Actor actor, CancellationToken ct);
    Task<EnvironmentView?> GetEnvironmentAsync(Actor actor, Guid id, CancellationToken ct);
}

public sealed class RequestRejectedException(int statusCode, string code, string? message = null)
    : System.ApplicationException(message ?? code)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record ValidatedOrder(string IdempotencyKey, Guid ProjectId, string DisplayName,
    Guid ArchiveVersionId, int TtlDays, string PayloadHash);

public static class OrderInput
{
    public static ValidatedOrder Validate(string idempotencyKey, CreateEnvironmentRequest request)
    {
        if (request is null || request.ProjectId == Guid.Empty || request.Source is null ||
            request.Source.ArchiveVersionId == Guid.Empty || request.Mode != "archive" ||
            request.DisplayName is null || request.DisplayName.Length is < 1 or > 120 ||
            string.IsNullOrWhiteSpace(request.DisplayName) ||
            request.DisplayName != request.DisplayName.Trim() ||
            request.DisplayName.Any(char.IsControl) || request.TtlDays is < 1 or > 36500 ||
            string.IsNullOrEmpty(idempotencyKey) || idempotencyKey.Length > 128 ||
            idempotencyKey.Any(c => c < 0x21 || c > 0x7e))
            throw new RequestRejectedException(400, "InvalidOrder");
        // JSON field recognition (including nested source fields) belongs to the strict API serializer.
        // Serialize values in a fixed order and hash UTF-8 bytes; no client-supplied paths are represented.
        var payload = JsonSerializer.Serialize(new
        {
            projectId = request.ProjectId.ToString("D"), displayName = request.DisplayName,
            mode = "archive", archiveVersionId = request.Source.ArchiveVersionId.ToString("D"), ttlDays = request.TtlDays
        });
        return new ValidatedOrder(idempotencyKey, request.ProjectId, request.DisplayName,
            request.Source.ArchiveVersionId, request.TtlDays,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
    }
}
