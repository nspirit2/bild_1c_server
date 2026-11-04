using System.Reflection;
using BuildSystem.Infrastructure;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class StageAdmissionTests
{
    private static bool Accept(string kind, string snapshot)
    {
        var type = typeof(SqlEnvironmentOrders).Assembly.GetType("BuildSystem.Infrastructure.StageAdmission", throwOnError: true)!;
        var method = type.GetMethod("IsStageJob", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(null, [new EnvironmentJob { Kind = kind, SnapshotJson = snapshot }])!;
    }

    private const string Stage = "{\"admissionMode\":\"archive-stage-only\",\"stageOnly\":true,\"executable\":false,\"scheduledJobsMustRemainBlocked\":true}";
    private const string Archive = "{\"admissionMode\":\"archive\",\"stageOnly\":false,\"executable\":false,\"scheduledJobsMustRemainBlocked\":true}";

    [Fact]
    public void Exact_discriminators_and_historical_archive_are_accepted()
    {
        Assert.True(Accept("ObserveArchiveStage", Stage));
        Assert.True(Accept("ProvisionArchive", Archive));
        Assert.True(Accept("ProvisionArchive", "{\"reservationId\":\"historical\"}"));
    }

    [Fact]
    public void Kind_change_cannot_promote_stage_only_or_downgrade_archive()
    {
        Assert.False(Accept("ProvisionArchive", Stage));
        Assert.False(Accept("ObserveArchiveStage", Archive));
        Assert.False(Accept("ObserveArchiveStage", "{}"));
        Assert.False(Accept("Unknown", Stage));
    }

    [Theory]
    [InlineData("stageOnly", "true", "false")]
    [InlineData("executable", "false", "true")]
    [InlineData("scheduledJobsMustRemainBlocked", "true", "false")]
    public void Unsafe_flags_are_rejected(string key, string before, string after)
    {
        Assert.False(Accept("ObserveArchiveStage", Stage.Replace($"\"{key}\":{before}", $"\"{key}\":{after}")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"stageOnly\":false}")]
    public void Malformed_or_partial_snapshot_is_rejected(string json)
    {
        Assert.False(Accept("ObserveArchiveStage", json));
        Assert.False(Accept("ProvisionArchive", json));
    }

    [Fact]
    public void Missing_wrong_type_and_ambiguous_markers_are_rejected()
    {
        Assert.False(Accept("ObserveArchiveStage", Stage.Replace("\"stageOnly\":true,", "")));
        Assert.False(Accept("ObserveArchiveStage", Stage.Replace("\"stageOnly\":true", "\"stageOnly\":\"true\"")));
        Assert.False(Accept("ObserveArchiveStage", Stage.Replace("\"stageOnly\":true", "\"stageOnly\":true,\"stageOnly\":true")));
        Assert.False(Accept("ObserveArchiveStage", Stage.Replace("\"stageOnly\":true", "\"stageOnly\":true,\"StageOnly\":true")));
        Assert.False(Accept("ProvisionArchive", "{\"StageOnly\":true}"));
    }
}
// end
