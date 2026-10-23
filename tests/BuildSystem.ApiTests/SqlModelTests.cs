using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class SqlModelTests
{
    private static BuildSystemDbContext Context() => new(new DbContextOptionsBuilder<BuildSystemDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);

    [Fact]
    public void MigrationAndCurrentModelMatchWithoutConnectingToSql()
    {
        using var db = Context();
        Assert.NotEmpty(db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges());
    }
    [Fact]
    public void MigrationGeneratesSqlWithAtomicityConstraints()
    {
        using var db = Context();
        var sql = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CREATE TABLE [EnvironmentRequests]", sql);
        Assert.Contains("Latin1_General_100_BIN2", sql);
        Assert.Contains("[RowVersion] rowversion", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_EnvironmentRequests_OwnerId_IdempotencyKey]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_EnvironmentReservations_SqlServerId_SqlDbName]", sql);
    }
}
// end
