using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class ArtifactReceiptInputTests
{
    private static BuildSystemDbContext Context() => new(new DbContextOptionsBuilder<BuildSystemDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
    private static ReportStagedArtifact Valid() => new(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), new string('A', 64), 10, "dt");

    [Fact]
    public async Task InvalidPayloadsRejectedBeforeSql()
    {
        var valid = Valid();
        var invalid = new[] { valid with { JobId = Guid.Empty }, valid with { FencingToken = 0 },
            valid with { ReservationId = Guid.Empty }, valid with { ArchiveVersionId = Guid.Empty },
            valid with { ArtifactId = Guid.Empty }, valid with { Sha256 = "bad" }, valid with { Sha256 = null! },
            valid with { SizeBytes = 0 }, valid with { Format = "zip" } };
        foreach (var report in invalid)
        {
            using var db = Context();
            var error = await Assert.ThrowsAsync<RequestRejectedException>(() =>
                new SqlArtifactReceipts(db).ReportAsync("token", report, CancellationToken.None));
            Assert.Equal(400, error.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptCannotBeModifiedOrDeletedThroughDbContext(bool delete)
    {
        using var db = Context();
        var record = new StagedArtifact { ArtifactId = Guid.NewGuid(), JobId = Guid.NewGuid(),
            AgentId = Guid.NewGuid(), ReservationId = Guid.NewGuid(), ArchiveVersionId = Guid.NewGuid(),
            Sha256 = new string('A', 64), SizeBytes = 10, Format = "dt", FencingToken = 1 };
        db.Attach(record);
        if (delete) db.Remove(record); else record.SizeBytes++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
