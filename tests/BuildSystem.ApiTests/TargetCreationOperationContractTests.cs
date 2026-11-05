using BuildSystem.Application;
using BuildSystem.Domain;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class TargetCreationOperationContractTests
{
    [Fact]
    public void LedgerModelKeepsRevisionCasAndRestrictsItsImmutableAnchorWithoutSqlConnection()
    {
        using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var entity = db.Model.FindEntityType(typeof(CreationOperationRecord))!;
        Assert.True(entity.FindProperty(nameof(CreationOperationRecord.Revision))!.IsConcurrencyToken);
        var anchor = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, anchor.DeleteBehavior);
        Assert.Equal(typeof(RestoreIntentRecord), anchor.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(CreationOperationRecord.RestoreIntentId), Assert.Single(anchor.Properties).Name);
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(CreationOperationRecord.RestoreIntentId) }));
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(CreationOperationRecord.AttemptId) }) &&
            i.GetFilter() == "[AttemptId] IS NOT NULL");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalStateCannotBeResetBeforeAnyDatabaseAccess(bool replaceAttempt)
    {
        await using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var row = new CreationOperationRecord { OperationId = Guid.NewGuid(), RestoreIntentId = Guid.NewGuid(),
            State = TargetCreationOperationState.ReconciliationRequired, Revision = 3,
            AttemptId = Guid.NewGuid(), AttemptExpectedRevision = 1,
            QuarantineReason = TargetCreationQuarantineReason.OutcomeUnknown };
        db.Attach(row);
        if (replaceAttempt) row.AttemptId = Guid.NewGuid();
        else { row.State = TargetCreationOperationState.Registered; row.Revision = 1; row.AttemptId = null;
            row.AttemptExpectedRevision = null; row.QuarantineReason = null; }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Equal("Creation ledger transition is not permitted.", error.Message);
    }

    [Fact]
    public async Task InternalReadDefaultsOffAndRejectsEmptyIdWithoutOpeningSqlConnection()
    {
        await using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var actor = new Actor(Guid.NewGuid(), true);
        var disabled = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            new SqlTargetCreationOperations(db, new()).GetAsync(actor, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(409, disabled.StatusCode); Assert.Equal("TargetCreationDisabled", disabled.Code);
        var invalid = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            new SqlTargetCreationOperations(db, new() { Enabled = true }).GetAsync(actor, Guid.Empty, CancellationToken.None));
        Assert.Equal(400, invalid.StatusCode); Assert.Equal("InvalidTargetCreation", invalid.Code);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void ReadAndWriteInterfacesShareScopedInternalServiceWithoutNewMutationMethods()
    {
        var services = new ServiceCollection();
        services.AddBuildSystemInfrastructure("Server=unused;Database=unused;Integrated Security=true");
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        Assert.Same(scope.ServiceProvider.GetRequiredService<ITargetCreationOperations>(),
            scope.ServiceProvider.GetRequiredService<ITargetCreationOperationReads>());
        Assert.False(scope.ServiceProvider.GetRequiredService<TargetCreationOperationOptions>().Enabled);
        Assert.Equal("GetAsync", Assert.Single(typeof(ITargetCreationOperationReads).GetMethods()).Name);
        Assert.DoesNotContain(typeof(ITargetCreationOperationReads).GetMethods(), m =>
            m.Name.Contains("Reset") || m.Name.Contains("Release") || m.Name.Contains("Execute"));
    }

    [Fact]
    public void InternalLedgerDefaultsOffAndExposesNoConfigurableExecutionFlag()
    {
        var options = new TargetCreationOperationOptions();
        Assert.False(options.Enabled);
        Assert.Empty(options.CanonicalMappings);
        var receipt = typeof(TargetCreationOperationReceipt);
        Assert.Null(receipt.GetProperty("Executable")!.SetMethod);
        Assert.Null(receipt.GetProperty("ScheduledJobsMustRemainBlocked")!.SetMethod);
        Assert.Equal(new[] { "BeginAttemptAsync", "QuarantineAsync", "RegisterAsync" },
            typeof(ITargetCreationOperations).GetMethods().Select(m => m.Name).Order().ToArray());
        Assert.DoesNotContain("Enabled", options.ToString());
    }
}
