using BuildSystem.Application;
using Xunit;

namespace BuildSystem.ApiTests;

public sealed class TargetCreationOperationContractTests
{
    [Fact]
    public void InternalLedgerDefaultsOffAndExposesNoConfigurableExecutionFlag()
    {
        var options = new TargetCreationOperationOptions();
        Assert.False(options.Enabled);
        Assert.Empty(options.CanonicalMappings);
        var receipt = typeof(TargetCreationOperationReceipt);
        Assert.Null(receipt.GetProperty("Executable")!.SetMethod);
        Assert.Null(receipt.GetProperty("ScheduledJobsMustRemainBlocked")!.SetMethod);
        Assert.Equal(new[] { "BeginAttemptAsync", "QuarantineAsync", "RegisterAsync" },
            typeof(ITargetCreationOperations).GetMethods().Select(m => m.Name).Order().ToArray());
        Assert.DoesNotContain("Enabled", options.ToString());
    }
}
