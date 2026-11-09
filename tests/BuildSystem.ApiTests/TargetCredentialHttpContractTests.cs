using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

// Offline HTTP contracts only: fake administrator authentication and no SQL-backed credential port.
public sealed class TargetCredentialHttpContractTests
{
    private const string Prefix = "/api/admin/target-credentials";
    private const string Sensitive = "dummy-secret-sensitive";
    private static readonly Guid CredentialId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class Factory(bool? enabled = true) : ApiFactory
    {
        public readonly CredentialsStub Credentials = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Development must not expose the original exception through its exception page.
            builder.UseEnvironment("Development");
            if (enabled.HasValue)
                builder.UseSetting("TargetCredentials:Enabled", enabled.Value.ToString());
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITargetCredentials>();
                services.AddSingleton<ITargetCredentials>(Credentials);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "CredentialContract";
                    options.DefaultChallengeScheme = "CredentialContract";
                    options.DefaultForbidScheme = "CredentialContract";
                }).AddScheme<AuthenticationSchemeOptions, StagingReleaseAuthentication>(
                    "CredentialContract", _ => { });
            });
        }
    }

    private sealed class CredentialsStub : ITargetCredentials
    {
        public int StoreCalls;
        public int GetCalls;
        public int RevokeCalls;
        public int Calls => StoreCalls + GetCalls + RevokeCalls;

        public Task<TargetCredentialMetadata> StoreAsync(Actor actor, TargetCredentialBinding binding,
            TargetCredentialInput input, CancellationToken ct)
        {
            StoreCalls++;
            throw new InvalidOperationException(Sensitive);
        }

        public Task<TargetCredentialMetadata> GetAsync(Actor actor, Guid credentialId, CancellationToken ct)
        {
            GetCalls++;
            throw new InvalidOperationException(Sensitive);
        }

        public Task<TargetCredentialMetadata> RevokeAsync(Actor actor, Guid credentialId,
            long expectedRevision, CancellationToken ct)
        {
            RevokeCalls++;
            throw new InvalidOperationException(Sensitive);
        }
    }

    private static async Task<HttpClient> Client(Factory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-Role", "Administrator");
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return client;
    }

    private static object Import() => new
    {
        credentialId = CredentialId,
        canonicalClusterId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        canonicalSqlServerId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
        endpointMappingId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
        clusterUser = "cluster-user-sensitive",
        clusterPassword = Sensitive,
        sqlUser = "sql-user-sensitive",
        sqlPassword = "sql-password-sensitive"
    };

    private static async Task SafeUnavailable(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Sensitive, body);
        Assert.DoesNotContain("cluster-user-sensitive", body);
        Assert.DoesNotContain("sql-user-sensitive", body);
        Assert.DoesNotContain("sql-password-sensitive", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CredentialId.ToString("D"), body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(new[] { "code" }, json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("TargetCredentialsUnavailable", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnexpectedGetFailureReturnsNeutral503WithoutSecretStackOrMetadata()
    {
        using var factory = new Factory();
        using var client = await Client(factory);
        using var response = await client.GetAsync($"{Prefix}/{CredentialId:D}");
        await SafeUnavailable(response);
        Assert.Equal(1, factory.Credentials.GetCalls);
        Assert.Equal(1, factory.Credentials.Calls);
    }

    [Fact]
    public async Task UnexpectedStoreFailureWithValidEightFieldBodyReturnsNeutral503()
    {
        using var factory = new Factory();
        using var client = await Client(factory);
        using var response = await client.PostAsJsonAsync(Prefix, Import());
        await SafeUnavailable(response);
        Assert.Equal(1, factory.Credentials.StoreCalls);
        Assert.Equal(1, factory.Credentials.Calls);
    }

    [Fact]
    public async Task UnexpectedRevokeFailureReturnsNeutral503WithoutSecretStackOrMetadata()
    {
        using var factory = new Factory();
        using var client = await Client(factory);
        using var response = await client.PostAsJsonAsync($"{Prefix}/{CredentialId:D}/revoke",
            new { expectedRevision = 1 });
        await SafeUnavailable(response);
        Assert.Equal(1, factory.Credentials.RevokeCalls);
        Assert.Equal(1, factory.Credentials.Calls);
    }

    [Fact]
    public async Task MissingEnableSettingDefaultsOffForAllRoutesWithoutCallingCredentialPort()
    {
        using var factory = new Factory(enabled: null);
        using var client = await Client(factory);
        using var get = await client.GetAsync($"{Prefix}/{CredentialId:D}");
        using var store = await client.PostAsJsonAsync(Prefix, Import());
        using var revoke = await client.PostAsJsonAsync($"{Prefix}/{CredentialId:D}/revoke",
            new { expectedRevision = 1 });
        await SafeUnavailable(get);
        await SafeUnavailable(store);
        await SafeUnavailable(revoke);
        Assert.Equal(0, factory.Credentials.Calls);
    }
}
