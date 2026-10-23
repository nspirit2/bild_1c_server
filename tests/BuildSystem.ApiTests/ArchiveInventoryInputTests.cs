using BuildSystem.Application;
using BuildSystem.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuildSystem.ApiTests;

// Validation must fail before touching SQL; no InMemory substitute for transactional tests.
public sealed class ArchiveInventoryInputTests
{
    private static SqlArchiveCatalog Catalog() => new(new BuildSystemDbContext(
        new DbContextOptionsBuilder<BuildSystemDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options));

    [Theory]
    [InlineData("../outside.dt")]
    [InlineData("/absolute.dt")]
    [InlineData("C:\\absolute.dt")]
    [InlineData("folder//archive.dt")]
    [InlineData("folder./archive.dt")]
    [InlineData("NUL.dt")]
    [InlineData("COM1/archive.dt")]
    [InlineData("file:stream.dt")]
    public async Task UnsafePathRejectedBeforeDatabase(string path)
    {
        var exception = await Assert.ThrowsAsync<RequestRejectedException>(() => Catalog().ReportAsync("token",
            new(Guid.NewGuid(), [new(path, new string('A', 64), 1, "dt")]), CancellationToken.None));
        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task MissingArrayOrNullEntryRejectedBeforeDatabase()
    {
        foreach (var entries in new ArchiveInventoryEntry[][] { null!, [null!] })
        {
            var exception = await Assert.ThrowsAsync<RequestRejectedException>(() => Catalog().ReportAsync("token",
                new(Guid.NewGuid(), entries), CancellationToken.None));
            Assert.Equal(400, exception.StatusCode);
        }
    }
}
