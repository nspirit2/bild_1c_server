using BuildSystem.Application;
using BuildSystem.Domain;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class TargetCreationPermitContractTests
{
    private static BuildSystemDbContext Context() => new(new DbContextOptionsBuilder<BuildSystemDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);

    [Fact]
    public void PermitDefaultsOffAndDiDoesNotEnableCreation()
    {
        var services = new ServiceCollection();
        services.AddBuildSystemInfrastructure("Server=unused;Database=unused;Integrated Security=true");
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        Assert.IsType<SqlTargetCreationPermits>(scope.ServiceProvider.GetRequiredService<ITargetCreationPermits>());
        Assert.False(scope.ServiceProvider.GetRequiredService<TargetCreationPermitOptions>().Enabled);
        Assert.False(scope.ServiceProvider.GetRequiredService<TargetCreationOperationOptions>().Enabled);
    }

    [Fact]
    public void PermitModelEnforcesOneOperationAttemptAndRevisionCas()
    {
        using var db = Context();
        var entity = db.Model.FindEntityType(typeof(CreationPermitRecord))!;
        Assert.True(entity.FindProperty(nameof(CreationPermitRecord.Revision))!.IsConcurrencyToken);
        var fk = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior);
        Assert.Equal(typeof(CreationOperationRecord), fk.PrincipalEntityType.ClrType);
        foreach (var key in new[] { nameof(CreationPermitRecord.OperationId), nameof(CreationPermitRecord.AttemptId) })
            Assert.Contains(entity.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(new[] { key }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EitherDisabledOptionRejectsAllCallsBeforeSql(bool permitEnabled, bool creationEnabled)
    {
        await using var db = Context();
        var service = new SqlTargetCreationPermits(db, new() { Enabled = permitEnabled }, new() { Enabled = creationEnabled });
        var actor = new Actor(Guid.NewGuid(), true); var id = Guid.NewGuid();
        foreach (var call in new Func<Task>[] {
            () => service.IssueAsync(actor, id, 1, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None),
            () => service.RecordStartAsync(actor, id, 1, Guid.NewGuid(), CancellationToken.None),
            () => service.RevokeAsync(actor, id, 1, CancellationToken.None),
            () => service.GetAsync(actor, id, CancellationToken.None) })
            Assert.Equal(409, (await Assert.ThrowsAsync<RequestRejectedException>(call)).StatusCode);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("deadline")]
    [InlineData("delete")]
    [InlineData("reset")]
    public async Task ImmutableAnchorsAndTerminalStateRejectBeforeSql(string mutation)
    {
        await using var db = Context(); var now = DateTimeOffset.UtcNow;
        var row = new CreationPermitRecord { PermitId = Guid.NewGuid(), OperationId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
            ExpectedOperationRevision = 1, IssuedAt = now, StartBefore = now.AddMinutes(5),
            State = TargetCreationPermitState.Revoked, Revision = 3, StartedAt = now, StartExpectedRevision = 1 };
        db.Attach(row);
        if (mutation == "attempt") row.AttemptId = Guid.NewGuid();
        else if (mutation == "deadline") row.StartBefore = now.AddMinutes(6);
        else if (mutation == "delete") db.Remove(row);
        else { row.State = TargetCreationPermitState.Prepared; row.Revision = 1; row.StartedAt = null; row.StartExpectedRevision = null; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
