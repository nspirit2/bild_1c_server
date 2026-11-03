using BuildSystem.Agent;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class TargetObservationHostCommandTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task DisabledReturnsBeforeDirectoryOriginCredentialOrWorkerSetup(string? enabled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:TargetObservation:Enabled"] = enabled,
            ["Agent:DataDirectory"] = "untrusted-relative-private-path",
            ["Agent:ApiOrigin"] = "http://private-secret@example.invalid",
            ["Agent:TargetObservation:RacExecutable"] = "invalid-private-path",
            ["Agent:ArchiveStageEnabled"] = "true"
        }).Build();
        using var output = new StringWriter();
        Assert.Equal(4, await TargetObservationHostCommand.RunAsync(Guid.NewGuid(), config, output));
        Assert.Equal("TargetObservationDisabled" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task MalformedConfigurationIsFixedRedactedFailure()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Agent:TargetObservation:Enabled"] = "private-secret-not-bool" }).Build();
        using var output = new StringWriter();
        Assert.Equal(5, await TargetObservationHostCommand.RunAsync(Guid.NewGuid(), config, output));
        Assert.Equal("TargetObservationConfigurationOrStorageFailure" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task InvalidAndCancelledRequestsDoNotBindConfiguration()
    {
        using var output = new StringWriter();
        Assert.Equal(64, await TargetObservationHostCommand.RunAsync(Guid.Empty, null!, output));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Equal(130, await TargetObservationHostCommand.RunAsync(Guid.NewGuid(), null!, output, cancel.Token));
        Assert.Equal("UnsupportedAgentCommand" + Environment.NewLine + "TargetObservationCancelled" + Environment.NewLine, output.ToString());
    }
}
// End.
