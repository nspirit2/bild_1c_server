using BuildSystem.Application;
using BuildSystem.Domain;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
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
