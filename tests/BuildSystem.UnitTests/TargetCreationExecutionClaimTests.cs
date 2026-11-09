using BuildSystem.Domain;
using Xunit;

namespace BuildSystem.UnitTests;

public sealed class TargetCreationExecutionClaimTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);
    private static readonly Guid[] Ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();

    private static TargetCreationExecutionClaim Issue(DateTimeOffset? now = null,
        DateTimeOffset? issued = null, DateTimeOffset? deadline = null, Guid[]? ids = null)
    {
        var x = ids ?? Ids;
        return TargetCreationExecutionClaim.Issue(x[0], x[1], x[2], x[3], x[4], x[5], x[6],
            issued ?? Start, deadline ?? Start.AddMinutes(5), now ?? Start);
    }

    private static void Error(string code, Action action) =>
        Assert.Equal(code, Assert.Throws<DomainRuleException>(action).Code);

    [Fact]
    public void ClaimIsOnlyHistoricalPinnedEvidenceWithoutExecutableOrResetState()
    {
        var row = Issue(Start.AddMinutes(4));
        Assert.Equal(Ids[0], row.ClaimId);
        Assert.Equal(Ids[1], row.PermitId);
        Assert.Equal(Ids[2], row.OperationId);
        Assert.Equal(Ids[3], row.AttemptId);
        Assert.Equal(Ids[4], row.AgentId);
        Assert.Equal(Ids[5], row.CredentialId);
        Assert.Equal(Ids[6], row.EndpointMappingId);
        Assert.Equal(Start.AddMinutes(4), row.IssuedAt);
        Assert.Equal(Start.AddMinutes(5), row.ExpiresAt); // Never a renewed deadline.
        Assert.DoesNotContain(typeof(TargetCreationExecutionClaim).GetProperties(),
            property => property.Name is "Executable" or "NewlyIssued" or "RetryAllowed" or "State" or "Revision");
    }

    [Fact]
    public void DeadlineIsExclusiveAndClaimCannotReopenFiveMinuteWindow()
    {
        Assert.Equal(Start, Issue().IssuedAt);
        Assert.Equal(Start.AddMinutes(5), Issue(Start.AddMinutes(5).AddTicks(-1)).ExpiresAt);
        Error("PermitExpired", () => Issue(Start.AddMinutes(5)));
        Error("PermitExpired", () => Issue(Start.AddMinutes(6)));
        Error("InvalidServerTime", () => Issue(Start.AddTicks(-1)));
        Error("InvalidPermitWindow", () => Issue(deadline: Start.AddMinutes(10)));
        Error("InvalidPermitWindow", () => Issue(deadline: Start.AddMinutes(4)));
    }

    [Fact]
    public void AllSevenIdentityPinsAreMandatory()
    {
        for (var index = 0; index < Ids.Length; index++)
        {
            var pins = (Guid[])Ids.Clone();
            pins[index] = Guid.Empty;
            Error("InvalidId", () => Issue(ids: pins));
        }
    }

    [Fact]
    public void EquivalentOffsetsAreNormalizedWithoutExtendingOriginalDeadline()
    {
        var row = Issue(Start.AddMinutes(4).ToOffset(TimeSpan.FromHours(-7)),
            Start.ToOffset(TimeSpan.FromHours(3)), Start.AddMinutes(5).ToOffset(TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.Zero, row.IssuedAt.Offset);
        Assert.Equal(TimeSpan.Zero, row.ExpiresAt.Offset);
        Assert.Equal(Start.AddMinutes(4), row.IssuedAt);
        Assert.Equal(Start.AddMinutes(5), row.ExpiresAt);
    }
}
