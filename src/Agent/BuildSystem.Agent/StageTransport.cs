using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public interface IStageTransport
{
    Task<StageLeaseGrant?> PollStageAsync(CancellationToken ct);
    Task<LeaseStatus> HeartbeatStageAsync(LeaseHeartbeat heartbeat, CancellationToken ct);
}

public sealed partial class AgentTransport
{
    public Task<StageLeaseGrant?> PollStageAsync(CancellationToken ct) =>
        StageRequestAsync<StageLeaseGrant>("api/agent/stage/poll", null, true, ct);

    public async Task<LeaseStatus> HeartbeatStageAsync(LeaseHeartbeat heartbeat, CancellationToken ct)
    {
        if (heartbeat is null || heartbeat.JobId == Guid.Empty || heartbeat.FencingToken <= 0)
            throw new ArgumentException("Invalid stage heartbeat.", nameof(heartbeat));
        return await StageRequestAsync<LeaseStatus>("api/agent/stage/heartbeat", heartbeat, false, ct)
            ?? throw new InvalidOperationException("Invalid stage response.");
    }

    private async Task<T?> StageRequestAsync<T>(string route, object? body, bool allowEmpty, CancellationToken ct) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (allowEmpty && response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("Stage API request rejected.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = new byte[16385];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count > 16384) throw new InvalidOperationException("Invalid stage response.");
        try
        {
            return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, count), new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw new InvalidOperationException("Invalid stage response.");
        }
        catch (JsonException) { throw new InvalidOperationException("Invalid stage response."); }
    }
}
// end
