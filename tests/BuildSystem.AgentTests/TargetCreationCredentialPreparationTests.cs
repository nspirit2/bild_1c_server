using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BuildSystem.Agent;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetCreationCredentialPreparationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "credential-preparation-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Issued = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string Canary = "secret-canary";
    private static TargetCreationIntent Intent()
    {
        var reservation = Guid.NewGuid(); var name = "bs_test_" + reservation.ToString("N");
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, reservation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), name, name);
    }
    private static TargetCreationPermitBinding Binding(TargetCreationIntent i) =>
        new(Guid.NewGuid(), i.OperationId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Issued, Issued.AddMinutes(5))
        { CredentialId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid() };
    private static void Seed(AgentJournal j, TargetCreationIntent i, TargetCreationPermitBinding b)
    { j.SaveTargetCreationIntent(i); j.SaveTargetCreationPermitBinding(i, b); }
    private static TargetCreationCredentialPreparation Helper(AgentJournal j, Starts s, Credentials c, bool enabled = true) =>
        new(j, s, c, new TargetCreationCredentialPreparationOptions { Enabled = enabled });
    private sealed class Starts : ITargetCreationPermitStartTransport
    {
        public int Calls;
        public Func<TargetCreationIntent, TargetCreationPermitBinding, CancellationToken, Task<TargetCreationPermitStartReceipt>> Send =
            (i, b, _) => Task.FromResult(new TargetCreationPermitStartReceipt(i, b, true, Issued.AddMinutes(1)));
        public Task<TargetCreationPermitStartReceipt> RecordStartAsync(TargetCreationIntent i, TargetCreationPermitBinding b, CancellationToken cancellationToken = default)
        { Calls++; return Send(i, b, cancellationToken); }
    }
    private sealed class Credentials : ITargetCreationCredentialTransport, IDisposable
    {
        private readonly TargetCreationCredentialHttpClient client = new(new Handler(),
            new() { Enabled = true, BaseUri = new Uri("https://credentials.example/") }, () => "machine-token");
        public int Calls;
        public TargetCreationCredentialRequest? Request;
        public TargetCreationDeliveredCredentials? Holder;
        public Action? Before;
        public Action? After;
        public bool Null;
        public async Task<TargetCreationDeliveredCredentials> ResolveAsync(TargetCreationCredentialRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Request = request; Before?.Invoke();
            if (Null) return null!;
            Holder = await client.ResolveAsync(request, cancellationToken); After?.Invoke(); return Holder;
        }
        public void Dispose() { Holder?.Dispose(); client.Dispose(); }
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var pins = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            var json = JsonSerializer.Serialize(new
            {
                permitId = pins.GetProperty("permitId").GetGuid(), expectedPermitRevision = 2,
                attemptId = pins.GetProperty("attemptId").GetGuid(), credentialId = pins.GetProperty("credentialId").GetGuid(),
                endpointMappingId = pins.GetProperty("endpointMappingId").GetGuid(),
                clusterUser = Canary + "-cluster-user", clusterPassword = Canary + "-cluster-password",
                sqlUser = Canary + "-sql-user", sqlPassword = Canary + "-sql-password",
                executable = false, scheduledJobsMustRemainBlocked = true, retryAllowed = false
            });
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(json, Encoding.UTF8, "application/json"),
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } } };
        }
    }
    private static void Inert(TargetCreationCredentialPreparationReceipt r)
    { Assert.False(r.Executable); Assert.False(r.FreshAuthority); Assert.False(r.RetryAllowed); Assert.True(r.ScheduledJobsMustRemainBlocked); }
    private static void SlotsRetained(AgentJournal j, TargetCreationIntent i)
    {
        Assert.Throws<InvalidOperationException>(() => j.SaveTargetCreationIntent(i with { OperationId = Guid.NewGuid(), CanonicalClusterId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => j.SaveTargetCreationIntent(i with { OperationId = Guid.NewGuid(), CanonicalSqlServerId = Guid.NewGuid() }));
    }
    private static void Fixed(Exception e, string code)
    { Assert.Equal(code, e.Message); Assert.Equal(code, e.ToString()); Assert.Null(e.InnerException); Assert.DoesNotContain(Canary, e.ToString()); }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Durable_begin_then_start_then_exact_one_resolution_leave_inflight_metadata(bool newlyRecorded)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b);
        var s = new Starts(); using var c = new Credentials(); var order = new List<string>();
        s.Send = (actual, binding, _) =>
        {
            Assert.Equal(i, actual); Assert.Equal(b, binding); order.Add("start");
            Assert.Equal(TargetCreationState.CreationInFlight, j.GetTargetCreationIntent(i.OperationId)!.State);
            using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")};Pooling=False"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT state || ':' || revision FROM target_creation_intents";
            Assert.Equal("CreationInFlight:2", cmd.ExecuteScalar()); return Task.FromResult(new TargetCreationPermitStartReceipt(i, b, newlyRecorded, Issued.AddMinutes(1)));
        };
        c.Before = () => { Assert.Equal(new[] { "start" }, order); order.Add("credentials"); Assert.Equal(2, j.GetTargetCreationIntent(i.OperationId)!.Revision); };
        using var r = await Helper(j, s, c).PrepareAsync(i, b);
        Assert.Equal("CredentialsPrepared", r.Outcome.ToString()); Assert.Equal(b, r.Binding); Assert.Equal(j.GetTargetCreationIntent(i.OperationId), r.Record);
        Assert.Equal(TargetCreationState.CreationInFlight, r.Record.State); Assert.Equal(2, r.Record.Revision);
        Assert.Equal(b.AttemptId, r.Record.AttemptId); Assert.Equal(1, r.Record.AttemptExpectedRevision);
        Assert.Equal(new TargetCreationCredentialRequest(b.PermitId, 2, b.AttemptId, b.CredentialId!.Value, b.EndpointMappingId!.Value), c.Request);
        Assert.Equal(new[] { "start", "credentials" }, order); Assert.Equal(1, s.Calls); Assert.Equal(1, c.Calls); Inert(r); SlotsRetained(j, i);
    }

    [Fact]
    public async Task Receipt_owns_holder_until_disposal_and_explicit_plaintext_escape_is_not_erased()
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); using var c = new Credentials();
        var r = await Helper(j, new Starts(), c).PrepareAsync(i, b); var escaped = r.ReadSecrets();
        Assert.Equal(Canary + "-cluster-user", escaped.ClusterUser); Assert.Equal(Canary + "-cluster-password", escaped.ClusterPassword);
        Assert.Equal(Canary + "-sql-user", escaped.SqlUser); Assert.Equal(Canary + "-sql-password", escaped.SqlPassword);
        Assert.Equal(escaped, c.Holder!.ReadSecrets()); r.Dispose(); r.Dispose();
        Assert.Throws<ObjectDisposedException>(() => r.ReadSecrets()); Assert.Throws<ObjectDisposedException>(() => c.Holder.ReadSecrets());
        Assert.Equal(Canary + "-sql-password", escaped.SqlPassword); Inert(r); Assert.Equal(2, j.GetTargetCreationIntent(i.OperationId)!.Revision);
    }

    [Fact]
    public async Task Receipt_denies_json_including_null_and_has_fixed_nonsecret_diagnostics()
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); using var c = new Credentials();
        using var r = await Helper(j, new Starts(), c).PrepareAsync(i, b);
        var diagnostic = r.ToString(); r.ReadSecrets(); Assert.Equal(diagnostic, r.ToString());
        Assert.DoesNotContain(Canary, diagnostic); Assert.DoesNotContain(i.OperationId.ToString(), diagnostic);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(r)); Assert.Throws<JsonException>(() => JsonSerializer.Serialize<object>(r));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize<TargetCreationCredentialPreparationReceipt>(null!));
        foreach (var json in new[] { "{}", "null", "{\"clusterPassword\":\"secret-canary\"}" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TargetCreationCredentialPreparationReceipt>(json));
        Assert.DoesNotContain(typeof(TargetCreationCredentialPreparationReceipt).GetProperties(), p => p.PropertyType == typeof(TargetCreationDeliveredCredentials) || p.PropertyType == typeof(TargetCreationDeliveredSecretValues));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Second_helper_never_restarts_or_resolves_even_after_owner_disposes(bool dispose)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); var s = new Starts(); using var c = new Credentials();
        using var first = await Helper(j, s, c).PrepareAsync(i, b); if (dispose) first.Dispose();
        using var second = await Helper(j, s, c).PrepareAsync(i, b);
        Assert.Equal("AlreadyAttempted", second.Outcome.ToString()); Assert.Equal(first.Record, second.Record); Inert(second);
        Assert.ThrowsAny<Exception>(() => second.ReadSecrets()); Assert.Equal(1, s.Calls); Assert.Equal(1, c.Calls);
        if (!dispose) Assert.Equal(Canary + "-sql-password", first.ReadSecrets().SqlPassword);
    }

    [Fact]
    public async Task Default_off_precedes_cancelled_token_and_disposed_journal()
    {
        var i = Intent(); var b = Binding(i); var j = new AgentJournal(directory); j.Dispose(); var s = new Starts(); using var c = new Credentials();
        using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.False(new TargetCreationCredentialPreparationOptions().Enabled);
        var e = await Assert.ThrowsAsync<TargetCreationCredentialPreparationException>(() => Helper(j, s, c, false).PrepareAsync(i, b, cts.Token));
        Fixed(e, "TargetCreationCredentialPreparationUnavailable"); Assert.Equal(0, s.Calls); Assert.Equal(0, c.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Precancel_does_not_access_journal_or_any_transport(bool disposed)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); if (disposed) j.Dispose();
        var s = new Starts(); using var c = new Credentials(); using var cts = new CancellationTokenSource(); cts.Cancel();
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Helper(j, s, c).PrepareAsync(i, b, cts.Token));
        Assert.Equal(cts.Token, e.CancellationToken); Assert.Equal(0, s.Calls); Assert.Equal(0, c.Calls);
        if (!disposed) Assert.Equal(1, j.GetTargetCreationIntent(i.OperationId)!.Revision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Failed_start_returns_reconciliation_without_resolving(bool cancel)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); using var cts = new CancellationTokenSource();
        var s = new Starts { Send = (_, _, _) => { if (cancel) cts.Cancel(); throw new InvalidOperationException(Canary); } }; using var c = new Credentials();
        using var r = await Helper(j, s, c).PrepareAsync(i, b, cts.Token);
        Assert.Equal("ReconciliationRequired", r.Outcome.ToString()); Assert.Equal(3, r.Record.Revision);
        Assert.Equal(cancel ? "Cancelled" : "OutcomeUnknown", r.Record.QuarantineReason); Assert.Equal(1, s.Calls); Assert.Equal(0, c.Calls); Inert(r); SlotsRetained(j, i);
    }

    [Theory]
    [InlineData("error")] [InlineData("caller-cancel")] [InlineData("unrelated-cancel")] [InlineData("null")] [InlineData("disposed")]
    public async Task Credential_failure_quarantines_exact_attempt_retains_slots_and_never_retries(string kind)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); using var cts = new CancellationTokenSource();
        var s = new Starts(); using var c = new Credentials { Null = kind == "null" };
        c.Before = () => { if (kind == "caller-cancel") cts.Cancel(); if (kind is "error" or "caller-cancel" or "unrelated-cancel") throw kind == "error" ? new InvalidOperationException(Canary) : new OperationCanceledException(Canary); };
        c.After = () => { if (kind == "disposed") c.Holder!.Dispose(); };
        using var r = await Helper(j, s, c).PrepareAsync(i, b, cts.Token);
        Assert.Equal("ReconciliationRequired", r.Outcome.ToString()); Assert.Equal(TargetCreationState.ReconciliationRequired, r.Record.State);
        Assert.Equal(3, r.Record.Revision); Assert.Equal(kind == "caller-cancel" ? "Cancelled" : "OutcomeUnknown", r.Record.QuarantineReason);
        Assert.Equal(b.AttemptId, r.Record.AttemptId); Assert.Equal(b, r.Binding); Assert.Equal(r.Record, j.GetTargetCreationIntent(i.OperationId)); Inert(r); SlotsRetained(j, i);
        using var replay = await Helper(j, s, c).PrepareAsync(i, b); Assert.Equal("AlreadyAttempted", replay.Outcome.ToString()); Assert.Equal(1, s.Calls); Assert.Equal(1, c.Calls);
    }

    [Fact]
    public async Task Cancellation_after_acquisition_disposes_holder_before_returning_reconciliation()
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); using var cts = new CancellationTokenSource();
        using var c = new Credentials { After = cts.Cancel }; using var r = await Helper(j, new Starts(), c).PrepareAsync(i, b, cts.Token);
        Assert.Equal("ReconciliationRequired", r.Outcome.ToString()); Assert.Equal("Cancelled", r.Record.QuarantineReason);
        Assert.Throws<ObjectDisposedException>(() => c.Holder!.ReadSecrets()); Assert.ThrowsAny<Exception>(() => r.ReadSecrets()); SlotsRetained(j, i);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Sqlite_quarantine_failure_is_fixed_unconfirmed_error_and_disposes_acquired_holder(bool acquired)
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); Seed(j, i, b); using var cts = new CancellationTokenSource();
        using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "agent-journal.sqlite")};Pooling=False"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = $"CREATE TRIGGER fail_quarantine BEFORE UPDATE ON target_creation_intents WHEN NEW.state='ReconciliationRequired' BEGIN SELECT RAISE(ABORT, '{Canary}'); END"; cmd.ExecuteNonQuery();
        var s = new Starts(); using var c = new Credentials(); if (acquired) c.After = cts.Cancel; else c.Before = () => throw new InvalidOperationException(Canary);
        var e = await Assert.ThrowsAsync<TargetCreationCredentialPreparationException>(() => Helper(j, s, c).PrepareAsync(i, b, cts.Token));
        Fixed(e, "TargetCreationCredentialPreparationReconciliationUnconfirmed"); Assert.Equal(TargetCreationState.CreationInFlight, j.GetTargetCreationIntent(i.OperationId)!.State);
        Assert.Equal(2, j.GetTargetCreationIntent(i.OperationId)!.Revision); if (acquired) Assert.Throws<ObjectDisposedException>(() => c.Holder!.ReadSecrets());
        Assert.Equal(1, s.Calls); Assert.Equal(1, c.Calls); SlotsRetained(j, i);
    }

    [Fact]
    public async Task Unbound_input_has_no_mutation_start_or_resolution()
    {
        var i = Intent(); var b = Binding(i); using var j = new AgentJournal(directory); j.SaveTargetCreationIntent(i); var s = new Starts(); using var c = new Credentials();
        var e = await Assert.ThrowsAsync<TargetCreationCredentialPreparationException>(() => Helper(j, s, c).PrepareAsync(i, b));
        Fixed(e, "TargetCreationCredentialPreparationUnavailable"); Assert.Equal(1, j.GetTargetCreationIntent(i.OperationId)!.Revision); Assert.Equal(0, s.Calls); Assert.Equal(0, c.Calls);
    }
}
