using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public interface ICleanupResultTransport
{
    Task<StageCleanupResultReceipt> ReportCleanupResultAsync(ReportStageCleanup report, CancellationToken ct);
}

public sealed class CleanupResultReceiptException : Exception
{
    public string Code => "ReceiptMismatch";
    public CleanupResultReceiptException() : base("ReceiptMismatch") { }
}

/// <summary>Only allowlisted definitive rejections may permanently block a durable result.</summary>
public sealed class CleanupResultConflictException : HttpRequestException
{
    public CleanupResultConflictException() : base("Cleanup result identity rejected.", null, HttpStatusCode.Conflict) { }
}
public sealed class CleanupResultInvalidException : HttpRequestException
{
    public CleanupResultInvalidException() : base("Cleanup result metadata rejected.", null, HttpStatusCode.BadRequest) { }
}

public sealed partial class AgentTransport : ICleanupResultTransport
{
    private static readonly JsonSerializerOptions CleanupReceiptJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<StageCleanupResultReceipt> ReportCleanupResultAsync(ReportStageCleanup report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.ResultId == Guid.Empty || report.AuthorizationId == Guid.Empty || report.RetirementId == Guid.Empty ||
            report.JobId == Guid.Empty || report.ArtifactId == Guid.Empty || report.FencingToken <= 0)
            throw new ArgumentException("Invalid cleanup result identity.", nameof(report));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/agent/stage/cleanup-result")
        { Content = JsonContent.Create(report) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            // Default-off is also HTTP 409. Status alone must never permanently block an outbox row.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
            {
                var errorBytes = await ReadCleanupBodyAsync(response, timeout.Token);
                if (errorBytes is not null)
                {
                    try
                    {
                        using var error = JsonDocument.Parse(errorBytes);
                        var fields = error.RootElement.ValueKind == JsonValueKind.Object ? error.RootElement.EnumerateObject().ToArray() : [];
                        if (fields.Length == 1 && fields[0].Name == "code" && fields[0].Value.ValueKind == JsonValueKind.String)
                        {
                            var code = fields[0].Value.GetString();
                            if (response.StatusCode == HttpStatusCode.Conflict && code == "CleanupResultMismatch") throw new CleanupResultConflictException();
                            if (response.StatusCode == HttpStatusCode.BadRequest && code == "InvalidCleanupResult") throw new CleanupResultInvalidException();
                        }
                    }
                    catch (JsonException) { /* Unknown error payload remains retryable. Never echo it. */ }
                }
            }
            throw new HttpRequestException("Cleanup result API request rejected.", null, response.StatusCode);
        }
        const int maximumBytes = 16 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = new byte[maximumBytes + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count > maximumBytes) throw new CleanupResultReceiptException();
        StageCleanupResultReceipt? receipt;
        try { receipt = JsonSerializer.Deserialize<StageCleanupResultReceipt>(bytes.AsSpan(0, count), CleanupReceiptJson); }
        catch (JsonException) { throw new CleanupResultReceiptException(); }
        ValidateCleanupReceipt(report, receipt);
        return receipt!;
    }

    private static async Task<byte[]?> ReadCleanupBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int maximumBytes = 16 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[maximumBytes + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) break;
            count += read;
        }
        return count is > 0 and <= maximumBytes ? bytes[..count] : null;
    }

    internal static void ValidateCleanupReceipt(ReportStageCleanup report, StageCleanupResultReceipt? receipt)
    {
        if (receipt is null || receipt.ResultId != report.ResultId || receipt.AuthorizationId != report.AuthorizationId ||
            receipt.ArtifactId != report.ArtifactId || receipt.State != "DeletedReported" || receipt.ReceivedAt == default)
            throw new CleanupResultReceiptException();
    }
}
// end
