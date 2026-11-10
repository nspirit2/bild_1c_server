using System.Net;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

// Offline real endpoint/client boundary: SQLite is local metadata, never CREATE authority.
public sealed class TargetCreationExecutionClaimInteropTests : IDisposable
{
    private const string Token = "interop-claim-token";
    private const string Canary = "private-server-failure-canary";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "claim-http-interop", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset SqlNow = new(2020, 1, 2, 3, 4, 0, TimeSpan.Zero);
    private static readonly string[] RequestFields = ["permitId", "expectedPermitRevision", "attemptId", "credentialId", "endpointMappingId"];
    private static readonly string[] ResponseFields = ["claimId", "permitId", "operationId", "attemptId", "agentId", "credentialId", "endpointMappingId", "issuedAt", "expiresAt", "newlyIssued", "executable", "scheduledJobsMustRemainBlocked", "retryAllowed"];

    private sealed class Port(TargetCreationIntent intent, TargetCreationPermitBinding binding) : ITargetCreationExecutionClaims
    {
        public int Calls, ValidatedCalls, SuccessfulCalls;
        public bool Fail, ExtendDeadline;
        public readonly Guid ClaimId = Guid.NewGuid();
        public Task<TargetCreationExecutionClaimReceipt> ClaimAsync(string token, Guid permitId, long revision,
            Guid attemptId, Guid credentialId, Guid endpointMappingId, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(Token, token); Assert.Equal(2, revision); Assert.Equal(binding.PermitId, permitId);
            Assert.Equal(binding.AttemptId, attemptId); Assert.Equal(binding.CredentialId, credentialId);
            Assert.Equal(binding.EndpointMappingId, endpointMappingId); ValidatedCalls++;
            if (Fail) throw new InvalidOperationException(Canary);
            if (Calls > 1) throw new RequestRejectedException(409, "TargetCreationExecutionClaimAlreadyIssued", Canary);
            var receipt = new TargetCreationExecutionClaimReceipt(ClaimId, permitId, intent.OperationId, attemptId,
                binding.AgentId, credentialId, endpointMappingId, SqlNow.AddMinutes(1), binding.StartBefore.AddMinutes(ExtendDeadline ? 1 : 0));
            SuccessfulCalls++; // Assertions swallowed by the API's sanitizing catch cannot count as success.
            return Task.FromResult(receipt);
        }
    }
    private sealed class Factory(Port port) : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            { ["TargetCreationExecutionClaimHttp:Enabled"] = "true", ["TargetCreationExecutionClaim:Enabled"] = "true" }));
            builder.ConfigureServices(s => { s.RemoveAll<ITargetCreationExecutionClaims>(); s.AddSingleton<ITargetCreationExecutionClaims>(port); });
        }
    }
    private sealed class Bridge(HttpClient server, bool inspectJson) : HttpMessageHandler
    {
        public int Calls, ResponsesProduced;
        public HttpStatusCode Status;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var original = request.RequestUri;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://localhost/api/agents/target-creation-execution-claims", original!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme); Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            using (var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
            {
                Assert.Equal(RequestFields.Order(), json.RootElement.EnumerateObject().Select(p => p.Name).Order());
                Assert.Equal(2, json.RootElement.GetProperty("expectedPermitRevision").GetInt64());
            }
            using var forwarded = new HttpRequestMessage(request.Method, original) { Content = request.Content };
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await server.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
            response.RequestMessage = request; request.RequestUri = original; // Preserve the client's trusted URI identity.
            Status = response.StatusCode;
            Assert.True(response.Headers.CacheControl?.NoStore);
            if (inspectJson && response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                Assert.Equal(ResponseFields, json.RootElement.EnumerateObject().Select(p => p.Name));
                Assert.True(json.RootElement.GetProperty("newlyIssued").GetBoolean());
                Assert.True(json.RootElement.GetProperty("executable").GetBoolean());
                Assert.True(json.RootElement.GetProperty("scheduledJobsMustRemainBlocked").GetBoolean());
                Assert.False(json.RootElement.GetProperty("retryAllowed").GetBoolean());
            }
            ResponsesProduced++;
            return response;
        }
    }
    private (TargetCreationIntent Intent, TargetCreationPermitBinding Binding, AgentJournal Journal, TargetCreationRecord Record) Seed()
    {
        var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
        var i = new TargetCreationIntent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, reservation,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
        var b = new TargetCreationPermitBinding(Guid.NewGuid(), i.OperationId, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), SqlNow, SqlNow.AddMinutes(5)) { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
        var journal = new AgentJournal(directory); journal.SaveTargetCreationIntent(i); journal.SaveTargetCreationPermitBinding(i, b);
        var record = journal.TryBeginTargetCreation(i.OperationId, 1, b.AttemptId).Record;
        TargetCreationLocalPreflight.ValidateInFlight(record, b);
        return (i, b, journal, record);
    }
    private static void Unchanged(AgentJournal journal, TargetCreationIntent i, TargetCreationPermitBinding b, TargetCreationRecord record)
    {
        Assert.Equal(record, journal.GetTargetCreationIntent(i.OperationId)); Assert.Equal(b, journal.GetTargetCreationPermitBinding(i.OperationId));
        Assert.Equal(TargetCreationState.CreationInFlight, record.State); Assert.Equal(2, record.Revision);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_JSON_grants_only_fresh_winner_with_exact_pins_and_SQL_clock(bool replay)
    {
        var (i, b, journal, record) = Seed(); using var local = journal;
        var port = new Port(i, b); using var factory = new Factory(port);
        using var server = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost/"), AllowAutoRedirect = false });
        var bridge = new Bridge(server, true); var tokens = 0;
        using var client = new TargetCreationExecutionClaimHttpClient(bridge,
            new() { Enabled = true, BaseUri = new Uri("https://localhost/"), TimeoutSeconds = 10 }, () => { tokens++; return Token; });
        var grant = await client.ClaimAsync(i, b, CancellationToken.None);
        Assert.Equal(i, grant.TargetIntent); Assert.Equal(b, grant.ScopeBinding); Assert.Equal(port.ClaimId, grant.ClaimId);
        Assert.Equal(SqlNow.AddMinutes(1), grant.IssuedAt); Assert.Equal(b.StartBefore, grant.ExpiresAt);
        Assert.True(grant.NewlyIssued); Assert.True(grant.Executable); Assert.True(grant.ScheduledJobsMustRemainBlocked); Assert.False(grant.RetryAllowed);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(grant));
        foreach (var id in new[] { i.OperationId, b.AgentId, b.CredentialId!.Value, port.ClaimId }) Assert.DoesNotContain(id.ToString(), grant.ToString());
        if (replay)
        {
            var error = await Assert.ThrowsAsync<TargetCreationExecutionClaimHttpException>(() => client.ClaimAsync(i, b, CancellationToken.None));
            Assert.Equal("TargetCreationExecutionClaimUnavailable", error.Message); Assert.Null(error.InnerException);
            Assert.Equal(HttpStatusCode.Conflict, bridge.Status);
        }
        Assert.Equal(replay ? 2 : 1, port.Calls); Assert.Equal(1, port.SuccessfulCalls);
        Assert.Equal(port.Calls, port.ValidatedCalls); Assert.Equal(port.Calls, bridge.Calls); Assert.Equal(port.Calls, bridge.ResponsesProduced); Assert.Equal(port.Calls, tokens);
        Unchanged(journal, i, b, record);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_shape_valid_deadline_extension_never_yields_authority_or_mutates_journal(bool extension)
    {
        var (i, b, journal, record) = Seed(); using var local = journal;
        var port = new Port(i, b) { Fail = !extension, ExtendDeadline = extension }; using var factory = new Factory(port);
        using var server = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost/"), AllowAutoRedirect = false });
        var bridge = new Bridge(server, false);
        using var client = new TargetCreationExecutionClaimHttpClient(bridge,
            new() { Enabled = true, BaseUri = new Uri("https://localhost/"), TimeoutSeconds = 10 }, () => Token);
        var error = await Assert.ThrowsAsync<TargetCreationExecutionClaimHttpException>(() => client.ClaimAsync(i, b, CancellationToken.None));
        Assert.Equal("TargetCreationExecutionClaimUnavailable", error.Message); Assert.Null(error.InnerException); Assert.DoesNotContain(Canary, error.ToString());
        Assert.Equal(extension ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, bridge.Status);
        Assert.Equal(1, port.Calls); Assert.Equal(1, port.ValidatedCalls); Assert.Equal(extension ? 1 : 0, port.SuccessfulCalls);
        Assert.Equal(1, bridge.Calls); Assert.Equal(1, bridge.ResponsesProduced); Unchanged(journal, i, b, record);
    }
    public void Dispose()
    {
        var absolute = Path.GetFullPath(directory); var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "claim-http-interop")) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(absolute) || !absolute.StartsWith(root, StringComparison.Ordinal) || Path.GetFileName(absolute).Length != 32)
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
