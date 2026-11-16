using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuildSystem.ApiTests;

// Offline machine HTTP boundary only: the fake port performs no SQL, RAC or target work.
public sealed class TargetRegistrationEvidenceReceiptEndpointTests
{
    private const string Route = "/api/agent/target-creation/registration-evidence";
    private const string Token = "receipt-token-not-secret";
    private const string Canary = "receipt-secret-canary-do-not-return";
    private const string Id = "abcdefab-1234-4567-89ab-abcdefabcdef";
    private const string OperationId = "abcdefab-2222-4567-89ab-abcdefabcdef";
    private const string SqlObservationId = "abcdefab-3333-4567-89ab-abcdefabcdef";
    private const string Profile = "rac-registration-8.3.22.2557-v1";
    private const string Date = "2026-11-13T10:11:12.1234567";
    private const string Invalid = "InvalidTargetRegistrationEvidenceReceipt";
    private static readonly string[] GuidFields = ["claimId", "reportedInfobaseId", "racObservationId", "sqlObservationId", "racEndpointMappingId", "sqlEndpointMappingId"];
    private static readonly string[] StringFields = ["registrationProfileId", "infobaseName", "sqlServer", "databaseName", "sqlCreateDate", "sqlState"];

    private static Dictionary<string, object?> Input() => new()
    {
        ["claimId"] = Id, ["reportedInfobaseId"] = Id, ["racObservationId"] = Id,
        ["sqlObservationId"] = SqlObservationId, ["racEndpointMappingId"] = Id, ["sqlEndpointMappingId"] = Id,
        ["registrationProfileId"] = Profile, ["infobaseName"] = "receipt_infobase", ["sqlServer"] = "sql.example.invalid",
        ["databaseName"] = "receipt_database", ["sqlDatabaseId"] = 42, ["sqlCreateDate"] = Date, ["sqlState"] = "ONLINE"
    };
    private static string Valid => JsonSerializer.Serialize(Input());
    private static string Changed(string name, object? value)
    {
        var input = Input(); input[name] = value; return JsonSerializer.Serialize(input);
    }
    private sealed class Port : ITargetRegistrationEvidenceReceipts
    {
        public int Calls;
        public string? Token;
        public ReportTargetRegistrationEvidence? Report;
        public Exception? Failure;
        public TargetRegistrationEvidenceReceipt Receipt = new(Guid.Parse(Id), Guid.Parse(OperationId), "Recorded", DateTimeOffset.Parse("2026-11-13T09:00:00+00:00"));
        public Task<TargetRegistrationEvidenceReceipt> ReportAsync(string agentToken, ReportTargetRegistrationEvidence report, CancellationToken ct)
        {
            Calls++; Token = agentToken; Report = report;
            return Failure is null ? Task.FromResult(Receipt) : throw Failure;
        }
    }
    private sealed class Factory(string? disabled = null, bool activationFailure = false, bool defaultGates = false) : ApiFactory
    {
        public readonly Port Port = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder); builder.UseEnvironment("Development");
            if (!defaultGates)
            {
                builder.UseSetting("TargetRegistrationEvidenceEndpoint:Enabled", (disabled != "TargetRegistrationEvidenceEndpoint:Enabled").ToString());
                builder.UseSetting("TargetRegistrationEvidenceReceipts:Enabled", (disabled != "TargetRegistrationEvidenceReceipts:Enabled").ToString());
            }
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITargetRegistrationEvidenceReceipts>();
                services.AddSingleton<ITargetRegistrationEvidenceReceipts>(_ => activationFailure ? throw new InvalidOperationException(Canary) : Port);
            });
        }
        public HttpClient Client(bool bearer = true, bool https = true)
        {
            var client = CreateClient(new() { BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"), AllowAutoRedirect = false });
            if (bearer) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TargetRegistrationEvidenceReceiptEndpointTests.Token);
            return client;
        }
    }
    private static StringContent Body(string? text = null) => new(text ?? Valid, Encoding.UTF8, "application/json");
    private static void Headers(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("no-cache", response.Headers.Pragma.Select(p => p.Name));
    }
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode); Headers(response);
        var text = await response.Content.ReadAsStringAsync();
        foreach (var secret in new[] { Canary, Token, Id, "InvalidOperationException", "stackTrace", "<html" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(new[] { "code" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }
    private static async Task RejectBody(Factory factory, HttpClient client, string body)
    {
        using var response = await client.PostAsync(Route, Body(body));
        await Error(response, HttpStatusCode.BadRequest, Invalid); Assert.Equal(0, factory.Port.Calls);
    }

    [Theory]
    [InlineData("TargetRegistrationEvidenceEndpoint:Enabled")]
    [InlineData("TargetRegistrationEvidenceReceipts:Enabled")]
    public async Task Either_gate_rejects_before_body_parsing_or_port_activation(string gate)
    {
        using var factory = new Factory(gate, activationFailure: true); using var client = factory.Client();
        foreach (var body in new[] { "{malformed", new string('x', 8193) })
        {
            using var response = await client.PostAsync(Route, Body(body));
            await Error(response, HttpStatusCode.Conflict, "TargetRegistrationEvidenceReceiptDisabled");
        }
        Assert.Equal(0, factory.Port.Calls);
    }
    [Fact]
    public async Task Both_gates_are_default_off()
    {
        Assert.False(new TargetRegistrationEvidenceReceiptOptions().Enabled);
        using var factory = new Factory(defaultGates: true); using var client = factory.Client();
        using var response = await client.PostAsync(Route, Body("not json"));
        await Error(response, HttpStatusCode.Conflict, "TargetRegistrationEvidenceReceiptDisabled"); Assert.Equal(0, factory.Port.Calls);
    }
    [Theory]
    [InlineData(Date)]
    [InlineData(Date + "Z")]
    [InlineData(Date + "+03:00")]
    public async Task Anonymous_machine_bearer_roundtrips_all_thirteen_fields_and_returns_only_inert_receipt(string sqlDate)
    {
        using var factory = new Factory(); using var client = factory.Client();
        using var response = await client.PostAsync(Route, Body(Changed("sqlCreateDate", sqlDate)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response); Assert.Equal(1, factory.Port.Calls);
        Assert.Equal(Token, factory.Port.Token);
        var report = Assert.IsType<ReportTargetRegistrationEvidence>(factory.Port.Report);
        Assert.All(new[] { report.ClaimId, report.ReportedInfobaseId, report.RacObservationId, report.RacEndpointMappingId, report.SqlEndpointMappingId }, id => Assert.Equal(Guid.Parse(Id), id));
        Assert.Equal(Guid.Parse(SqlObservationId), report.SqlObservationId);
        Assert.Equal(Profile, report.RegistrationProfileId); Assert.Equal("receipt_infobase", report.InfobaseName);
        Assert.Equal("sql.example.invalid", report.SqlServer); Assert.Equal("receipt_database", report.DatabaseName);
        Assert.Equal(42, report.SqlDatabaseId); Assert.Equal(sqlDate, report.SqlCreateDate); Assert.Equal("ONLINE", report.SqlState);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = json.RootElement;
        Assert.Equal(new[] { "acceptedAt", "claimId", "executable", "freshAuthority", "operationId", "ownershipEstablished", "requiresIndependentVerification", "restoreAllowed", "retryAllowed", "scheduledJobsMustRemainBlocked", "status" }, root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Guid.Parse(Id), root.GetProperty("claimId").GetGuid()); Assert.Equal(Guid.Parse(OperationId), root.GetProperty("operationId").GetGuid());
        Assert.Equal("Recorded", root.GetProperty("status").GetString()); Assert.Equal(factory.Port.Receipt.AcceptedAt, root.GetProperty("acceptedAt").GetDateTimeOffset());
        foreach (var name in new[] { "executable", "freshAuthority", "ownershipEstablished", "restoreAllowed", "retryAllowed" }) Assert.False(root.GetProperty(name).GetBoolean());
        foreach (var name in new[] { "requiresIndependentVerification", "scheduledJobsMustRemainBlocked" }) Assert.True(root.GetProperty(name).GetBoolean());
    }
    [Theory]
    [InlineData(null)]
    [InlineData("Basic fixture")]
    [InlineData("bearer receipt-token-not-secret")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("Bearer receipt-token-not-secret extra")]
    [InlineData("Bearer receipt-token-not-secret,other")]
    [InlineData("Bearer receipt-token-not-secret\tother")]
    [InlineData("Bearer receipt-token-not-secreté")]
    public async Task Missing_or_nonexact_bearer_never_reaches_port(string? authorization)
    {
        using var factory = new Factory(); using var client = factory.Client(false);
        if (authorization is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        using var response = await client.PostAsync(Route, Body()); await Error(response, HttpStatusCode.Unauthorized, "AgentUnauthorized"); Assert.Equal(0, factory.Port.Calls);
    }
    [Fact]
    public async Task Duplicate_and_overlong_bearer_are_rejected()
    {
        using var factory = new Factory();
        using (var client = factory.Client(false))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", new[] { "Bearer " + Token, "Bearer " + Token });
            using var response = await client.PostAsync(Route, Body()); await Error(response, HttpStatusCode.Unauthorized, "AgentUnauthorized");
        }
        using (var client = factory.Client(false))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + new string('a', 257));
            using var response = await client.PostAsync(Route, Body()); await Error(response, HttpStatusCode.Unauthorized, "AgentUnauthorized");
        }
        Assert.Equal(0, factory.Port.Calls);
    }
    [Fact]
    public async Task Http_is_rejected_without_redirect_even_with_forwarded_https()
    {
        using var factory = new Factory(); using var client = factory.Client(https: false); client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        using var response = await client.PostAsync(Route, Body()); await Error(response, HttpStatusCode.BadRequest, "TargetRegistrationEvidenceReceiptHttpsRequired");
        Assert.Null(response.Headers.Location); Assert.Equal(0, factory.Port.Calls);
    }
    [Theory]
    [InlineData("Origin", "https://localhost")]
    [InlineData("Origin", "null")]
    [InlineData("Cookie", "__Host-BuildSystem=fixture")]
    public async Task Browser_credentials_and_origin_are_rejected_even_with_valid_bearer(string name, string value)
    {
        using var factory = new Factory(); using var client = factory.Client(); client.DefaultRequestHeaders.Add(name, value);
        using var response = await client.PostAsync(Route, Body());
        await Error(response, name == "Cookie" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, name == "Cookie" ? "AgentUnauthorized" : "TargetRegistrationEvidenceReceiptForbidden");
        Assert.Equal(0, factory.Port.Calls);
    }
    [Fact]
    public async Task Administrator_cookie_identity_cannot_replace_machine_bearer()
    {
        using var factory = new Factory(); using var client = factory.Client(false);
        client.DefaultRequestHeaders.Add("X-Test-User", "yes"); client.DefaultRequestHeaders.Add("Cookie", "__Host-BuildSystem=fixture");
        using var response = await client.PostAsync(Route, Body()); await Error(response, HttpStatusCode.Unauthorized, "AgentUnauthorized"); Assert.Equal(0, factory.Port.Calls);
    }
    [Fact]
    public async Task Query_is_not_an_alternate_report_channel()
    {
        using var factory = new Factory(); using var client = factory.Client();
        using var response = await client.PostAsync(Route + "?token=" + Canary, Body()); await Error(response, HttpStatusCode.BadRequest, Invalid); Assert.Equal(0, factory.Port.Calls);
    }
    [Fact]
    public async Task Json_shape_is_exact_case_sensitive_flat_and_duplicate_free()
    {
        using var factory = new Factory(); using var client = factory.Client();
        foreach (var text in new[] { "", "null", "[]", "{}", "true", "42", Valid + "{}", Valid[..^1], Valid[..^1] + ",\"unknown\":true}", Valid[..^1] + ",\"claimId\":\"" + Id + "\"}", Valid.Replace("claimId", "ClaimId"), Valid.Replace("\"sqlDatabaseId\":42", "\"sqlDatabaseId\":{}") })
            await RejectBody(factory, client, text);
        foreach (var name in Input().Keys)
        {
            var missing = Input(); missing.Remove(name); await RejectBody(factory, client, JsonSerializer.Serialize(missing));
        }
    }
    [Fact]
    public async Task Every_guid_must_be_nonempty_lowercase_canonical_D()
    {
        using var factory = new Factory(); using var client = factory.Client();
        foreach (var field in GuidFields)
            foreach (var value in new object?[] { null, 1, Guid.Empty.ToString("D"), Id.ToUpperInvariant(), Id.Replace("-", ""), "{" + Id + "}", " " + Id, Id + " " })
                await RejectBody(factory, client, Changed(field, value));
    }
    [Fact]
    public async Task Rac_and_sql_observations_must_have_distinct_ids()
    {
        using var factory = new Factory(); using var client = factory.Client();
        await RejectBody(factory, client, Changed("sqlObservationId", Id));
        await RejectBody(factory, client, Changed("racObservationId", SqlObservationId));
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483648")]
    [InlineData("42.0")]
    [InlineData("4.2e1")]
    [InlineData("\"42\"")]
    [InlineData("null")]
    [InlineData("true")]
    public async Task Database_id_is_strict_positive_int32_json_integer(string value)
    {
        using var factory = new Factory(); using var client = factory.Client();
        await RejectBody(factory, client, Valid.Replace("\"sqlDatabaseId\":42", "\"sqlDatabaseId\":" + value));
    }
    [Fact]
    public async Task Strings_reject_null_empty_edge_whitespace_controls_and_nonstring_values()
    {
        using var factory = new Factory(); using var client = factory.Client();
        foreach (var field in StringFields)
            foreach (var value in new object?[] { null, 1, "", " ", "\t", "\n", "\u0000", " before", "after ", "a\tb", "a\nb" })
                await RejectBody(factory, client, Changed(field, value));
    }
    [Fact]
    public async Task String_limits_profile_state_and_date_are_not_normalized()
    {
        using var factory = new Factory(); using var client = factory.Client();
        foreach (var field in new[] { "infobaseName", "databaseName" }) await RejectBody(factory, client, Changed(field, new string('a', 129)));
        await RejectBody(factory, client, Changed("sqlServer", new string('a', 256)));
        await RejectBody(factory, client, Changed("registrationProfileId", Profile.ToUpperInvariant()));
        await RejectBody(factory, client, Changed("sqlState", "online"));
        foreach (var date in new[] { "2026-11-13", "2026-11-13T10:11:12", "2026-11-13T10:11:12.123456", "2026-02-30T10:11:12.1234567", Date + "+03:00x" }) await RejectBody(factory, client, Changed("sqlCreateDate", date));
    }
    [Fact]
    public async Task Inclusive_string_and_database_id_boundaries_reach_port_unchanged()
    {
        using var factory = new Factory(); using var client = factory.Client();
        var input = Input(); input["infobaseName"] = new string('i', 128); input["databaseName"] = new string('d', 128);
        input["sqlServer"] = new string('s', 255); input["sqlDatabaseId"] = int.MaxValue;
        using var response = await client.PostAsync(Route, Body(JsonSerializer.Serialize(input)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Headers(response); Assert.Equal(1, factory.Port.Calls);
        Assert.Equal(input["infobaseName"], factory.Port.Report!.InfobaseName);
        Assert.Equal(input["databaseName"], factory.Port.Report.DatabaseName); Assert.Equal(input["sqlServer"], factory.Port.Report.SqlServer);
        Assert.Equal(int.MaxValue, factory.Port.Report.SqlDatabaseId);
    }
    private sealed class Chunked(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
    [Fact]
    public async Task Declared_and_chunked_body_limits_are_eight_KiB_inclusive()
    {
        using var factory = new Factory(); using var client = factory.Client();
        var text = Valid + new string(' ', 8193 - Encoding.UTF8.GetByteCount(Valid));
        using var chunked = new Chunked(Encoding.UTF8.GetBytes(text)); chunked.Headers.ContentType = new("application/json");
        using (var response = await client.PostAsync(Route, chunked)) await Error(response, HttpStatusCode.RequestEntityTooLarge, "TargetRegistrationEvidenceReceiptBodyTooLarge");
        using (var response = await client.PostAsync(Route, Body(text))) await Error(response, HttpStatusCode.RequestEntityTooLarge, "TargetRegistrationEvidenceReceiptBodyTooLarge");
        Assert.Equal(0, factory.Port.Calls);
        using var exact = await client.PostAsync(Route, Body(text[..^1])); Assert.Equal(HttpStatusCode.OK, exact.StatusCode); Headers(exact); Assert.Equal(1, factory.Port.Calls);
    }
    [Fact]
    public async Task Only_uncompressed_utf8_application_json_is_accepted()
    {
        using var factory = new Factory(); using var client = factory.Client();
        using var badUtf8 = new ByteArrayContent([0x7b, 0xff, 0x7d]); badUtf8.Headers.ContentType = new("application/json");
        using (var response = await client.PostAsync(Route, badUtf8)) await Error(response, HttpStatusCode.BadRequest, Invalid);
        using var plain = new StringContent(Valid, Encoding.UTF8, "text/plain");
        using (var response = await client.PostAsync(Route, plain)) await Error(response, HttpStatusCode.BadRequest, Invalid);
        using var charset = Body(); charset.Headers.ContentType!.CharSet = "utf-16";
        using (var response = await client.PostAsync(Route, charset)) await Error(response, HttpStatusCode.BadRequest, Invalid);
        using var compressed = Body(); compressed.Headers.ContentEncoding.Add("gzip");
        using (var response = await client.PostAsync(Route, compressed)) await Error(response, HttpStatusCode.BadRequest, Invalid);
        Assert.Equal(0, factory.Port.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_port_and_activation_failures_are_sanitized_in_development(bool activation)
    {
        using var factory = new Factory(activationFailure: activation); factory.Port.Failure = new InvalidOperationException(Canary);
        using var client = factory.Client(); using var response = await client.PostAsync(Route, Body());
        await Error(response, HttpStatusCode.ServiceUnavailable, "TargetRegistrationEvidenceReceiptUnavailable"); Assert.Equal(activation ? 0 : 1, factory.Port.Calls);
    }
    [Fact]
    public async Task Arbitrary_rejection_code_is_not_reflected_or_given_its_requested_status()
    {
        using var factory = new Factory(); factory.Port.Failure = new RequestRejectedException(409, Canary, Canary);
        using var client = factory.Client(); using var response = await client.PostAsync(Route, Body());
        await Error(response, HttpStatusCode.ServiceUnavailable, "TargetRegistrationEvidenceReceiptUnavailable"); Assert.Equal(1, factory.Port.Calls);
    }
    [Fact]
    public async Task Malformed_receipt_is_never_exposed_as_accepted_history()
    {
        using var factory = new Factory(); using var client = factory.Client(); var good = factory.Port.Receipt;
        foreach (var receipt in new[] { null!, good with { ClaimId = Guid.Empty }, good with { ClaimId = Guid.Parse(OperationId) },
            good with { OperationId = Guid.Empty }, good with { Status = "Executable" }, good with { AcceptedAt = default },
            good with { AcceptedAt = good.AcceptedAt.ToOffset(TimeSpan.FromHours(3)) } })
        {
            factory.Port.Receipt = receipt;
            using var response = await client.PostAsync(Route, Body());
            await Error(response, HttpStatusCode.ServiceUnavailable, "TargetRegistrationEvidenceReceiptUnavailable");
        }
        Assert.Equal(7, factory.Port.Calls);
    }
    [Theory]
    [InlineData(401, "AgentUnauthorized")]
    [InlineData(403, "TargetRegistrationEvidenceReceiptForbidden")]
    [InlineData(409, "TargetRegistrationEvidenceReceiptMismatch")]
    public async Task Known_port_rejections_use_only_safe_code_pipeline(int status, string code)
    {
        using var factory = new Factory(); factory.Port.Failure = new RequestRejectedException(status, code, Canary);
        using var client = factory.Client(); using var response = await client.PostAsync(Route, Body());
        await Error(response, (HttpStatusCode)status, code); Assert.Equal(1, factory.Port.Calls);
    }
}
