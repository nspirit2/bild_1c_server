using BuildSystem.Application;
using BuildSystem.Contracts;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class StageDisabledTests
{
    [Fact]
    public async Task DefaultOptionsRejectPollAndHeartbeatBeforeDatabaseAccess()
    {
        using var db = new BuildSystemDbContext(new DbContextOptionsBuilder<BuildSystemDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var service = new SqlArchiveStageControl(db, new ArchiveStageOptions());
        var poll = await Assert.ThrowsAsync<RequestRejectedException>(async () => await service.PollAsync("credential", default));
        var beat = await Assert.ThrowsAsync<RequestRejectedException>(async () => await service.HeartbeatAsync("credential", new LeaseHeartbeat(Guid.NewGuid(), 1), default));
        Assert.Equal(409, poll.StatusCode);
        Assert.Equal(409, beat.StatusCode);
        Assert.Equal("StageDisabled", poll.Code);
        Assert.Equal("StageDisabled", beat.Code);
        var completion = new SqlStageCompletion(db, new ArchiveStageOptions());
        var end = await Assert.ThrowsAsync<RequestRejectedException>(async () => await completion.CompleteAsync("credential",
            new CompleteStage(Guid.NewGuid(), 1, Guid.NewGuid()), default));
        Assert.Equal(409, end.StatusCode);
        Assert.Equal("StageDisabled", end.Code);
    }
}
// end
