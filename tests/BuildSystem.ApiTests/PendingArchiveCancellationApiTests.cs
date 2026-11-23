using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BuildSystem.ApiTests;

// TestServer + real Identity cookie/antiforgery middleware. No SQL, user store or live agents.
public sealed class PendingArchiveCancellationApiTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateTimeOffset CancelledAt = DateTimeOffset.UnixEpoch.AddDays(8765);
    private sealed class Stub : IPendingArchiveCancellations
    {
        public List<(Actor, Guid, PendingArchiveCancellationRequest)> Calls { get; } = [];
        public RequestRejectedException? Rejection { get; set; }
        public Func<PendingArchiveCancellationReceipt, PendingArchiveCancellationReceipt> Result { get; set; } = x => x;
        public Task<PendingArchiveCancellationReceipt> CancelAsync(Actor actor, Guid environmentId,
            PendingArchiveCancellationRequest input, CancellationToken ct)
        {
            Calls.Add((actor, environmentId, input));
            if (Rejection is not null) throw Rejection;
            return Task.FromResult(Result(new(environmentId, Guid.NewGuid(), input.ExpectedJobId,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), actor.UserId, CancelledAt)));
        }
    }

    private sealed class Factory(string? enabled = "true", bool agentDefault = false) : ApiFactory
    {
        public Stub Port { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (enabled is not null) builder.UseSetting("PendingArchiveCancellation:Enabled", enabled);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPendingArchiveCancellations>();
                services.AddSingleton<IPendingArchiveCancellations>(Port);
                services.RemoveAll<IAgentControl>();
                services.AddScoped<IAgentControl, StubAgentControl>();
                services.Configure<AuthenticationOptions>(o =>
                {
                    o.DefaultAuthenticateScheme = agentDefault ? "AgentToken" : IdentityConstants.ApplicationScheme;
                    o.DefaultChallengeScheme = o.DefaultForbidScheme = o.DefaultAuthenticateScheme;
                });
            });
        }
        public HttpClient Client(bool human = true, bool admin = false)
        {
            var origin = new Uri("https://localhost");
            var jar = new CookieContainer();
            if (human)
            {
                var scheme = IdentityConstants.ApplicationScheme;
                var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
                var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, UserId.ToString("D")),
                    new(ClaimTypes.Name, "operator"), new(ClaimTypes.Role, admin ? "Administrator" : "Developer")], scheme);
                var now = DateTimeOffset.UtcNow;
                var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
                    { IssuedUtc = now, ExpiresUtc = now.AddMinutes(5) }, scheme);
                jar.Add(origin, new Cookie(options.Cookie.Name!, options.TicketDataFormat.Protect(ticket), "/") { Secure = true });
            }
            return CreateDefaultClient(origin, new CookieContainerHandler(jar));
        }
    }

    private static string Route(Guid id) => $"/api/test-environments/{id:D}/pending-archive-cancellations";
    private static Task<HttpResponseMessage> Post(HttpClient client, Guid env, Guid job) =>
        client.PostAsJsonAsync(Route(env), new { expectedJobId = job });
    private static async Task Csrf(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("token").GetString()!);
    }
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var field = Assert.Single(body.EnumerateObject());
        Assert.Equal("code", field.Name);
        Assert.Equal(code, field.Value.GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyIdentityCookieCanAuthorizeEvenWithCsrf(bool agent)
    {
        // Make the production agent scheme the default so an unqualified RequireAuthorization would accept it.
        using var factory = new Factory(agentDefault: agent);
        using var client = factory.Client(human: false);
        if (agent) client.DefaultRequestHeaders.Authorization = new("Bearer", "valid-test-token");
        await Csrf(client);
        using var response = await Post(client, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Port.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptCsrfIsRejectedBeforeService(bool corrupt)
    {
        using var factory = new Factory(); using var client = factory.Client();
        if (corrupt)
        {
            await Csrf(client);
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "not-the-token");
        }
        using var response = await Post(client, Guid.NewGuid(), Guid.NewGuid());
        await Error(response, HttpStatusCode.BadRequest, "InvalidCsrfToken");
        Assert.Empty(factory.Port.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task DisabledDoesNotDelegate(string? setting)
    {
        using var factory = new Factory(setting); using var client = factory.Client(); await Csrf(client);
        using var response = await Post(client, Guid.NewGuid(), Guid.NewGuid());
        await Error(response, HttpStatusCode.Conflict, "PendingArchiveCancellationDisabled");
        Assert.Empty(factory.Port.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"expectedJobId\":null}")]
    [InlineData("{\"expectedJobId\":\"not-a-guid\"}")]
    [InlineData("{\"expectedJobId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"ExpectedJobId\":\"JOB\"}")]
    [InlineData("{\"expectedJobId\":\"JOB\",\"expectedJobId\":\"JOB\"}")]
    [InlineData("{\"expectedJobId\":\"JOB\",\"actorId\":\"JOB\"}")]
    [InlineData("{\"expectedJobId\":\"JOB\"}{}")]
    public async Task StrictBodyRejectsBeforePort(string template)
    {
        using var factory = new Factory(); using var client = factory.Client(); await Csrf(client);
        using var body = new StringContent(template.Replace("JOB", Guid.NewGuid().ToString("D")), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(Route(Guid.NewGuid()), body);
        await Error(response, HttpStatusCode.BadRequest, "InvalidPendingArchiveCancellation");
        Assert.Empty(factory.Port.Calls);
    }

    [Fact]
    public async Task EmptyRouteIdIsNotAValidPin()
    {
        using var factory = new Factory(); using var client = factory.Client(); await Csrf(client);
        using var response = await Post(client, Guid.Empty, Guid.NewGuid());
        await Error(response, HttpStatusCode.BadRequest, "InvalidPendingArchiveCancellation");
        Assert.Empty(factory.Port.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForwardsExactActorAndPinsAndProjectsOnlyPublicReceipt(bool admin)
    {
        using var factory = new Factory(); using var client = factory.Client(admin: admin); await Csrf(client);
        var env = Guid.NewGuid(); var job = Guid.NewGuid();
        using var response = await Post(client, env, job);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal((new Actor(UserId, admin), env, new PendingArchiveCancellationRequest(job)), Assert.Single(factory.Port.Calls));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "cancelledAt", "code", "environmentId", "jobId" },
            json.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(env, json.GetProperty("environmentId").GetGuid());
        Assert.Equal(job, json.GetProperty("jobId").GetGuid());
        Assert.Equal("CancelledBeforeFirstLease", json.GetProperty("code").GetString());
        Assert.Equal(CancelledAt, json.GetProperty("cancelledAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "PendingArchiveCancellationNotFound")]
    [InlineData(HttpStatusCode.Conflict, "PendingArchiveCancellationMismatch")]
    public async Task ApplicationRejectionReturnsOnlyPublicCode(HttpStatusCode status, string code)
    {
        using var factory = new Factory(); using var client = factory.Client(); await Csrf(client);
        factory.Port.Rejection = new RequestRejectedException((int)status, code, "private application detail");
        using var response = await Post(client, Guid.NewGuid(), Guid.NewGuid());
        await Error(response, status, code);
        Assert.Single(factory.Port.Calls);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("job")]
    [InlineData("timestamp")]
    public async Task UnconfirmedReceiptCannotProduceSuccess(string mismatch)
    {
        using var factory = new Factory(); using var client = factory.Client(); await Csrf(client);
        factory.Port.Result = receipt => mismatch switch
        {
            "environment" => receipt with { EnvironmentId = Guid.NewGuid() },
            "job" => receipt with { JobId = Guid.NewGuid() },
            _ => receipt with { CancelledAt = default }
        };
        using var response = await Post(client, Guid.NewGuid(), Guid.NewGuid());
        await Error(response, HttpStatusCode.ServiceUnavailable, "PendingArchiveCancellationUnconfirmed");
        Assert.Single(factory.Port.Calls);
    }
}
