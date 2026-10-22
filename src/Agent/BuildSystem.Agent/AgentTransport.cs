using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BuildSystem.Contracts;

namespace BuildSystem.Agent;

public sealed class AgentTransport : IAgentTransport, IDisposable
{
    private readonly HttpClient client;
    public AgentTransport(Uri baseAddress, string token)
    {
        if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(baseAddress.UserInfo) || !string.IsNullOrEmpty(baseAddress.Query) ||
            !string.IsNullOrEmpty(baseAddress.Fragment) || baseAddress.AbsolutePath != "/")
            throw new ArgumentException("An HTTPS origin without credentials/path/query is required.");
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid credential format.");
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task CapabilitiesAsync(CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("api/agent/capabilities", new AgentCapabilities(["ControlPlaneProbe"]), ct);
        RequireSuccess(response);
    }
    public async Task<LeaseGrant?> PollAsync(CancellationToken ct)
    {
        using var response = await client.PostAsync("api/agent/poll", null, ct);
        RequireSuccess(response);
        return response.StatusCode == HttpStatusCode.NoContent ? null :
            await response.Content.ReadFromJsonAsync<LeaseGrant>(ct) ?? throw new InvalidOperationException("Missing lease response.");
    }
    public async Task<LeaseStatus> HeartbeatAsync(LeaseHeartbeat heartbeat, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("api/agent/heartbeat", heartbeat, ct);
        RequireSuccess(response);
        return await response.Content.ReadFromJsonAsync<LeaseStatus>(ct) ?? throw new InvalidOperationException("Missing lease status.");
    }
    public async Task ReportAsync(ReportAgentEvent report, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("api/agent/events", report, ct);
        RequireSuccess(response);
        var receipt = await response.Content.ReadFromJsonAsync<EventReceipt>(ct);
        if (receipt?.EventId != report.EventId) throw new InvalidOperationException("Event receipt mismatch.");
    }
    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Agent API request rejected.", null, response.StatusCode);
    }
    public void Dispose() => client.Dispose();
}
// end
