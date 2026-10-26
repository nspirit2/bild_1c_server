using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BuildSystem.Contracts;

[assembly: InternalsVisibleTo("BuildSystem.AgentTests")]

namespace BuildSystem.Agent;

public sealed partial class AgentTransport : IAgentTransport, IArtifactTransport, IStageTransport, IDisposable
{
    private readonly HttpClient client;
    public AgentTransport(Uri baseAddress, string token)
        : this(baseAddress, token, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    internal AgentTransport(Uri baseAddress, string token, HttpMessageHandler handler)
    {
        if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(baseAddress.UserInfo) || !string.IsNullOrEmpty(baseAddress.Query) ||
            !string.IsNullOrEmpty(baseAddress.Fragment) || baseAddress.AbsolutePath != "/")
            throw new ArgumentException("An HTTPS origin without credentials/path/query is required.");
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid credential format.");
        client = new HttpClient(handler)
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
    public async Task<StagedArtifactReceipt> ReportArtifactAsync(ReportStagedArtifact report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.JobId == Guid.Empty || report.FencingToken <= 0 || report.ReservationId == Guid.Empty ||
            report.ArchiveVersionId == Guid.Empty || report.ArtifactId == Guid.Empty ||
            report.Sha256 is null || report.Sha256.Length != 64 || !report.Sha256.All(Uri.IsHexDigit) ||
            report.SizeBytes <= 0 || report.Format is not ("dt" or "bak"))
            throw new ArgumentException("Invalid artifact metadata.", nameof(report));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var requestToken = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/agent/artifacts/staged")
        { Content = JsonContent.Create(report) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("Artifact API request rejected.", null, response.StatusCode);

        // Do not buffer untrusted server content: even a Content-Length header can lie.
        const int maximumReceiptBytes = 16 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
        var bytes = new byte[maximumReceiptBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), requestToken);
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count > maximumReceiptBytes)
            throw new ArtifactReceiptException("ReceiptMismatch");
        StagedArtifactReceipt? receipt;
        try { receipt = JsonSerializer.Deserialize<StagedArtifactReceipt>(bytes.AsSpan(0, count),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { throw new ArtifactReceiptException("ReceiptMismatch"); }
        if (receipt?.ArtifactId != report.ArtifactId || receipt.State != "ContentVerified")
            throw new ArtifactReceiptException("ReceiptMismatch");
        return receipt;
    }

    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Agent API request rejected.", null, response.StatusCode);
    }
    public void Dispose() => client.Dispose();
}
// end
