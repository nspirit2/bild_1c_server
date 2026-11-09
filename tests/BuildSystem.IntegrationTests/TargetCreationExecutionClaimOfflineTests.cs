using System.Data.Common;
using BuildSystem.Application;
using BuildSystem.Domain;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace BuildSystem.IntegrationTests;

// Real SQL Server provider/model and real service, but no SQL Server or execution is permitted.
public sealed class TargetCreationExecutionClaimOfflineTests
{
    private sealed class NoIo : DbConnectionInterceptor
    {
        public int Opens;
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        { Opens++; throw new InvalidOperationException("Unexpected SQL connection attempt"); }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { Opens++; throw new InvalidOperationException("Unexpected SQL connection attempt"); }
    }
    private sealed class Commands : DbCommandInterceptor
    {
        public int Count;
        public override InterceptionResult<DbCommand> CommandCreating(CommandCorrelatedEventData eventData, InterceptionResult<DbCommand> result)
        { Count++; return result; }
    }
    private static BuildSystemDbContext Context(NoIo io, Commands commands) => new(new DbContextOptionsBuilder<BuildSystemDbContext>()
        .UseSqlServer("Server=127.0.0.1,1;Database=offline_claims;User Id=broken;Password=broken;Connect Timeout=1;Encrypt=False")
        .AddInterceptors(io, commands).Options);
    private static SqlTargetCreationExecutionClaims Service(BuildSystemDbContext db, int disabled = -1) => new(db,
        new() { Enabled = disabled != 0 },
        new() { Enabled = disabled != 1, AgentStartEnabled = disabled != 2, CredentialBindingEnabled = disabled != 3 },
        new() { Enabled = disabled != 4 }, new() { Enabled = disabled != 5 });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task Each_independent_gate_rejects_before_any_io(int disabled)
    {
        var io = new NoIo(); var commands = new Commands(); using var db = Context(io, commands);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => Service(db, disabled).ClaimAsync(
            "token", Guid.NewGuid(), 2, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(409, error.StatusCode); Assert.Equal("TargetCreationExecutionClaimDisabled", error.Code);
        Assert.Equal(0, io.Opens); Assert.Equal(0, commands.Count);
    }

    [Fact]
    public async Task Default_options_are_disabled_before_io()
    {
        Assert.False(new TargetCreationExecutionClaimOptions().Enabled);
        var io = new NoIo(); var commands = new Commands(); using var db = Context(io, commands);
        var service = new SqlTargetCreationExecutionClaims(db, new(), new(), new(), new());
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => service.ClaimAsync(
            "token", Guid.NewGuid(), 2, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(409, error.StatusCode); Assert.Equal(0, io.Opens); Assert.Equal(0, commands.Count);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task All_gates_enabled_invalid_id_or_revision_rejects_before_io(bool invalidId)
    {
        var io = new NoIo(); var commands = new Commands(); using var db = Context(io, commands);
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => Service(db).ClaimAsync("token",
            invalidId ? Guid.Empty : Guid.NewGuid(), invalidId ? 2 : 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(400, error.StatusCode); Assert.Equal("InvalidTargetCreationExecutionClaim", error.Code);
        Assert.Equal(0, io.Opens); Assert.Equal(0, commands.Count);
    }

    [Fact]
    public void Sql_model_has_unique_immutable_evidence_not_an_executable_column()
    {
        var io = new NoIo(); var commands = new Commands(); using var db = Context(io, commands);
        var model = db.Model.FindEntityType(typeof(TargetCreationExecutionClaim))!;
        Assert.Equal(nameof(TargetCreationExecutionClaim.ClaimId), Assert.Single(model.FindPrimaryKey()!.Properties).Name);
        Assert.Equal(3, model.GetIndexes().Count(x => x.IsUnique));
        foreach (var name in new[] { "PermitId", "OperationId", "AttemptId" })
            Assert.Contains(model.GetIndexes(), x => x.IsUnique && x.Properties.Count == 1 && x.Properties[0].Name == name);
        Assert.All(model.GetProperties(), x => Assert.Equal(PropertySaveBehavior.Throw, x.GetAfterSaveBehavior()));
        Assert.Equal(3, model.GetForeignKeys().Count());
        Assert.All(model.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Restrict, x.DeleteBehavior));
        Assert.Null(model.FindProperty("Executable"));
        Assert.NotEqual(db.Model.FindEntityType(typeof(CreationPermitRecord))!.GetTableName(), model.GetTableName());
        Assert.NotNull(db.Model.FindEntityType(typeof(CreationPermitRecord))!.FindProperty("Executable"));
        Assert.Equal(0, io.Opens); Assert.Equal(0, commands.Count);
    }

    [Theory]
    [InlineData(EntityState.Modified)] [InlineData(EntityState.Deleted)] [InlineData(EntityState.Added)]
    public async Task Append_only_and_malformed_added_guards_fail_before_sql(EntityState state)
    {
        var io = new NoIo(); var commands = new Commands(); using var db = Context(io, commands);
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var claim = TargetCreationExecutionClaim.Issue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now, now.AddMinutes(5), now);
        var entry = db.Attach(claim);
        if (state == EntityState.Added)
        {
            entry.State = state;
            entry.Property(x => x.EndpointMappingId).CurrentValue = Guid.Empty;
        }
        else entry.State = state;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal(0, io.Opens); Assert.Equal(0, commands.Count);
    }
}
