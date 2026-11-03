using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class RestoreIntentModelTests
{
    private static BuildSystemDbContext Context() => new(new DbContextOptionsBuilder<BuildSystemDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);

    [Fact]
    public void ModelAndMigrationEnforcePermanentNonExecutableUniqueHold()
    {
        using var db = Context();
        Assert.False(db.Database.HasPendingModelChanges());
        var model = db.Model.FindEntityType(typeof(RestoreIntentRecord))!;
        foreach (var key in new[] { "EnvironmentId", "StageJobId", "ReservationId", "ArtifactId", "TargetIncarnation" })
            Assert.Contains(model.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { key }));
        var sql = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CK_RestoreIntents_NonExecutable", sql);
        Assert.Contains("CK_RestoreIntents_ScheduledJobsBlocked", sql);
        Assert.Contains("IX_RestoreIntents_CanonicalClusterId_InfobaseName", sql);
        Assert.Contains("IX_RestoreIntents_CanonicalSqlServerId_SqlDbName", sql);
        Assert.Contains("[SnapshotSha256] nvarchar(64)", sql);
    }

    [Fact]
    public async Task ModifiedAndDeletedHoldsAreRejectedBeforeDatabaseAccess()
    {
        using var db = Context();
        var row = new RestoreIntentRecord { RestoreIntentId = Guid.NewGuid() };
        db.Attach(row);
        row.SqlDbName = "changed";
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Entry(row).State = EntityState.Deleted;
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DefaultOptionsAndInfrastructureRegistrationAreFailClosed()
    {
        var services = new ServiceCollection();
        services.AddBuildSystemInfrastructure("Server=unused;Database=unused;Integrated Security=true");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.False(provider.GetRequiredService<RestoreIntentOptions>().Enabled);
        var service = scope.ServiceProvider.GetRequiredService<IRestoreIntents>();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => service.RequestAsync(
            new Actor(Guid.NewGuid(), false), Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()), default));
        Assert.Equal("RestoreIntentDisabled", error.Code);
    }

    [Fact]
    public void DiagnosticsAreRedactedAndReceiptCannotGrantExecution()
    {
        var secret = "bs_test_sensitive";
        var receipt = new RestoreIntentReceipt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), secret, secret, DateTimeOffset.UtcNow);
        Assert.False(receipt.Executable);
        Assert.DoesNotContain(secret, receipt.ToString());
        Assert.DoesNotContain(secret, new RestoreIntentRecord { SqlDbName = secret }.ToString());
    }
}
// end
