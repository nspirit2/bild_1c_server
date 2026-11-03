using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class TargetObservationModelTests
{
    private static BuildSystemDbContext Context() => new(new DbContextOptionsBuilder<BuildSystemDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);

    [Fact]
    public void ModelAndMigrationKeepAssignmentsImmutableAndNonExecutable()
    {
        using var db = Context();
        Assert.False(db.Database.HasPendingModelChanges());
        var model = db.Model.FindEntityType(typeof(TargetObservationRecord))!;
        Assert.Contains(model.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "RestoreIntentId" }));
        Assert.Contains(model.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "OperationId" }));
        var script = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CK_TargetObservations_NonExecutable", script);
        Assert.Contains("CK_TargetObservations_ScheduledJobsBlocked", script);
    }

    [Fact]
    public async Task ModifiedAndDeletedAssignmentsFailBeforeDatabaseAccess()
    {
        using var db = Context();
        var row = new TargetObservationRecord { ObservationRequestId = Guid.NewGuid() };
        db.Attach(row);
        row.FencingToken = 12;
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Entry(row).State = EntityState.Deleted;
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task DefaultRegistrationIsDisabledWithoutDatabaseAccess()
    {
        var services = new ServiceCollection();
        services.AddBuildSystemInfrastructure("Server=unused;Database=unused;Integrated Security=true");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.False(provider.GetRequiredService<TargetObservationOptions>().Enabled);
        var service = scope.ServiceProvider.GetRequiredService<ITargetObservations>();
        var error = await Assert.ThrowsAsync<RequestRejectedException>(() => service.RequestAsync(
            new Actor(Guid.NewGuid(), false), Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal("TargetObservationDisabled", error.Code);
    }
}
// End.
