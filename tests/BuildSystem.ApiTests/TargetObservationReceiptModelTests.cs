using System.Text.Json;
using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class TargetObservationReceiptModelTests
{
    private static BuildSystemDbContext Context() => new(new DbContextOptionsBuilder<BuildSystemDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);

    [Fact]
    public void ModelAndMigrationRetainOneReceiptPerRequestAndDistinctObservationIds()
    {
        using var db = Context();
        Assert.False(db.Database.HasPendingModelChanges());
        var model = db.Model.FindEntityType(typeof(TargetObservationReceiptRecord))!;
        foreach (var key in new[] { "ObservationRequestId", "RacObservationId", "SqlObservationId" })
            Assert.Contains(model.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { key }));
        var script = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CK_TargetObservationReceipts_NonExecutable", script);
        Assert.Contains("CK_TargetObservationReceipts_ScheduledJobsBlocked", script);
    }

    [Fact]
    public async Task ModifiedOrDeletedReceiptsRejectSyncAndAsyncSave()
    {
        using var db = Context();
        var row = new TargetObservationReceiptRecord { ReceiptId = Guid.NewGuid() };
        db.Attach(row);
        row.Outcome = 3;
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Entry(row).State = EntityState.Deleted;
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DefaultRegistrationRejectsReportsWithoutDatabaseAccess()
    {
        var services = new ServiceCollection();
        services.AddBuildSystemInfrastructure("Server=unused;Database=unused;Integrated Security=true");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() =>
            scope.ServiceProvider.GetRequiredService<ITargetObservationReceipts>().ReportAsync("unused", null!, default));
        Assert.Equal("TargetObservationDisabled", error.Code);
    }

    [Fact]
    public void AcknowledgementCannotInjectExecutionOrDisableJobsPolicy()
    {
        var ack = new TargetObservationReceipt(Guid.NewGuid(), Guid.NewGuid(), "Recorded");
        var json = JsonSerializer.Serialize(ack).Replace("\"Executable\":false", "\"Executable\":true")
            .Replace("\"ScheduledJobsMustRemainBlocked\":true", "\"ScheduledJobsMustRemainBlocked\":false");
        var restored = JsonSerializer.Deserialize<TargetObservationReceipt>(json)!;
        Assert.Equal(ack, restored);
        Assert.False(restored.Executable);
        Assert.True(restored.ScheduledJobsMustRemainBlocked);
        Assert.DoesNotContain(ack.ReceiptId.ToString(), ack.ToString());
    }
}
// End.
