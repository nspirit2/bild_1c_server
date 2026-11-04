using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildSystem.Application;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class StageOnlyOrderInputTests
{
    private static CreateEnvironmentRequest Fixture(string mode = "archive") => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"), "Archive fixture", mode,
        new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")), 7);

    [Fact]
    public void ArchiveHashPreservesOriginalSerializationExactly()
    {
        var request = Fixture();
        // This is the original archive serializer, including its field order and string GUIDs.
        var oldPayload = JsonSerializer.Serialize(new
        {
            projectId = request.ProjectId.ToString("D"), displayName = request.DisplayName,
            mode = "archive", archiveVersionId = request.Source.ArchiveVersionId.ToString("D"), ttlDays = request.TtlDays
        });
        const string oldFixture = "{\"projectId\":\"11111111-2222-3333-4444-555555555555\",\"displayName\":\"Archive fixture\",\"mode\":\"archive\",\"archiveVersionId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"ttlDays\":7}";
        Assert.Equal(oldFixture, oldPayload);
        var oldHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(oldPayload)));

        var validated = OrderInput.Validate("same-key", request);

        Assert.Equal(oldHash, validated.PayloadHash);
        Assert.False(validated.StageOnly);
    }

    [Fact]
    public void StageOnlyHashesItsOwnModeWithTheSameIdempotencyKey()
    {
        var archive = OrderInput.Validate("same-key", Fixture());
        var stageOnly = OrderInput.Validate("same-key", Fixture("archive-stage-only"));
        const string stageFixture = "{\"projectId\":\"11111111-2222-3333-4444-555555555555\",\"displayName\":\"Archive fixture\",\"mode\":\"archive-stage-only\",\"archiveVersionId\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"ttlDays\":7}";
        var stageHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stageFixture)));

        Assert.Equal(archive.IdempotencyKey, stageOnly.IdempotencyKey);
        Assert.NotEqual(archive.PayloadHash, stageOnly.PayloadHash);
        Assert.Equal(stageHash, stageOnly.PayloadHash);
        Assert.True(stageOnly.StageOnly);
        Assert.False(archive.StageOnly);
        Assert.Equal(stageOnly.PayloadHash,
            OrderInput.Validate("another-key", Fixture("archive-stage-only")).PayloadHash);
    }

    [Theory]
    [InlineData("Archive")]
    [InlineData("ARCHIVE")]
    [InlineData(" archive")]
    [InlineData("archive ")]
    [InlineData("archive\t")]
    [InlineData("Archive-stage-only")]
    [InlineData("ARCHIVE-STAGE-ONLY")]
    [InlineData(" archive-stage-only")]
    [InlineData("archive-stage-only ")]
    [InlineData("archive-stage-only\n")]
    [InlineData("archive-stage-onyl")]
    [InlineData("archive-stage")]
    [InlineData("archive_stage_only")]
    [InlineData("archiv")]
    [InlineData("")]
    [InlineData(null)]
    public void ModesRejectCaseWhitespaceAndTypos(string? mode)
    {
        var rejected = Assert.Throws<RequestRejectedException>(() =>
            OrderInput.Validate("key", Fixture() with { Mode = mode! }));

        Assert.Equal(400, rejected.StatusCode);
        Assert.Equal("InvalidOrder", rejected.Code);
    }

    [Fact]
    public void ValidatedOrderExistingConstructorDefaultsStageOnlyToFalse()
    {
        var request = Fixture();
        var validated = new ValidatedOrder("key", request.ProjectId, request.DisplayName,
            request.Source.ArchiveVersionId, request.TtlDays, "existing-hash");

        Assert.False(validated.StageOnly);
    }
}
