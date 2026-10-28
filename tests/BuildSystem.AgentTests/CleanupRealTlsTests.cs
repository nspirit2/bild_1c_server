using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BuildSystem.Agent;
using BuildSystem.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BuildSystem.AgentTests;

// Real TCP/TLS contracts. Trust is pinned inside this test's injected handler only;
// production AgentTransport's public constructor and OS certificate stores are unchanged.
public sealed class CleanupRealTlsTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static ReportStageCleanup Input() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7);

    [Fact]
    public async Task Pinned_real_tls_sends_exact_bearer_and_six_field_body_and_returns_receipt()
    {
        var input = Input();
        var expected = new StageCleanupResultReceipt(input.ResultId, input.AuthorizationId, input.ArtifactId,
            "DeletedReported", DateTimeOffset.UtcNow);
        var calls = 0;
        await using var server = await TlsServer.StartAsync(async context =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("Bearer " + Token, context.Request.Headers.Authorization.ToString());
            Assert.Equal("POST", context.Request.Method);
            Assert.False(context.Request.Headers.ContainsKey("Cookie"));
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var names = body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "artifactId", "authorizationId", "fencingToken", "jobId", "resultId", "retirementId" }, names);
            Assert.Equal(input, body.RootElement.Deserialize<ReportStageCleanup>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await context.Response.WriteAsJsonAsync(expected, context.RequestAborted);
        });
        using var transport = new AgentTransport(server.Origin, Token, PinnedHandler(server.Certificate.RawData));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, await transport.ReportCleanupResultAsync(input, deadline.Token));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Wrong_certificate_pin_rejects_real_tls_before_server_action()
    {
        var calls = 0;
        await using var server = await TlsServer.StartAsync(context =>
        {
            Interlocked.Increment(ref calls);
            return context.Response.WriteAsJsonAsync(new { unexpected = true }, context.RequestAborted);
        });
        using var otherCertificate = NewCertificate();
        using var transport = new AgentTransport(server.Origin, Token, PinnedHandler(otherCertificate.RawData));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.ReportCleanupResultAsync(Input(), deadline.Token));
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Real_tls_redirect_is_not_followed_and_preserves_307_status()
    {
        var calls = 0;
        await using var server = await TlsServer.StartAsync(context =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal("Bearer " + Token, context.Request.Headers.Authorization.ToString());
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            // A different host/port must never receive the credential or require a network connection.
            context.Response.Headers.Location = "https://localhost:1/credential-trap";
            return Task.CompletedTask;
        });
        using var transport = new AgentTransport(server.Origin, Token, PinnedHandler(server.Certificate.RawData));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => transport.ReportCleanupResultAsync(Input(), deadline.Token));
        Assert.Equal(HttpStatusCode.TemporaryRedirect, error.StatusCode);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    private static HttpClientHandler PinnedHandler(byte[] expectedDer) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        // A matching identity and exact certificate are required. Only self-signed chain errors
        // are allowed: name mismatch, missing certificate and every other error remain fatal.
        ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            certificate is not null && certificate.RawData.AsSpan().SequenceEqual(expectedDer) &&
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
    };

    private static X509Certificate2 NewCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Windows Server 2019 Schannel cannot always acquire server credentials from an
        // EphemeralKeySet key. UserKeySet without PersistKeySet gives Schannel a temporary
        // user key container removed on certificate disposal; no certificate store is used.
        var keyStorage = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, keyStorage);
    }

    private sealed class TlsServer(WebApplication app, X509Certificate2 certificate, Uri origin) : IAsyncDisposable
    {
        public X509Certificate2 Certificate { get; } = certificate;
        public Uri Origin { get; } = origin;

        public static async Task<TlsServer> StartAsync(RequestDelegate action)
        {
            var certificate = NewCertificate();
            WebApplication? app = null;
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
                app = builder.Build();
                app.MapPost("/api/agent/stage/cleanup-result", action);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StartAsync(deadline.Token);
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return new TlsServer(app, certificate, new Uri(address));
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                certificate.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StopAsync(deadline.Token);
            }
            finally
            {
                try { await app.DisposeAsync(); }
                finally { Certificate.Dispose(); }
            }
        }
    }
}
// end
