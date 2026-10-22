using BuildSystem.Domain;
using Xunit;

namespace BuildSystem.UnitTests;

public sealed class EnvironmentQuotaPolicyTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Pool = Guid.NewGuid();
    private static readonly EnvironmentQuotaLimits Limits = new(2, 1, 1000, TimeSpan.FromDays(7));
    private static readonly PoolBudget Budget = new(true, 2000, 1000, 2);
    private static EnvironmentReservation Proposed(long bytes = 100) => new(Guid.NewGuid(), Owner, Pool, bytes,
        new("sql", "new-db"), new("cluster", "new-ib"));
    private static EnvironmentQuotaEntry Entry(EnvironmentState state, long bytes = 100, Guid? owner = null,
        Guid? pool = null, bool unsettled = false) =>
        new(Guid.NewGuid(), owner ?? Owner, pool ?? Pool, bytes, state, unsettled);
    private static QuotaDecision Decide(IReadOnlyCollection<EnvironmentQuotaEntry> entries,
        EnvironmentQuotaLimits? limits = null, PoolBudget? budget = null, long bytes = 100) =>
        EnvironmentQuotaPolicy.CanReserve(Proposed(bytes), TimeSpan.FromDays(7), limits ?? Limits,
            budget ?? Budget, entries);

    [Theory]
    [InlineData(EnvironmentState.Requested)]
    [InlineData(EnvironmentState.Provisioning)]
    [InlineData(EnvironmentState.Preparing)]
    [InlineData(EnvironmentState.Ready)]
    [InlineData(EnvironmentState.Failed)]
    [InlineData(EnvironmentState.Expired)]
    [InlineData(EnvironmentState.Deleting)]
    [InlineData(EnvironmentState.NeedsAttention)]
    public void EveryNonDeletedReservationCountsTowardCountAndStorage(EnvironmentState state)
    {
        var entries = new[] { Entry(state), Entry(state) };
        Assert.Equal("EnvironmentCountExceeded", Decide(entries).RejectionCode);
        Assert.Equal("OwnerStorageExceeded", Decide([Entry(state, 950)],
            Limits with { MaxConcurrentProvisioning = 5 }).RejectionCode);
    }

    [Fact]
    public void OnlyDeletedReleasesQuota()
    {
        Assert.True(Decide([Entry(EnvironmentState.Deleted, 2000), Entry(EnvironmentState.Deleted, 2000)]).Allowed);
    }

    [Theory]
    [InlineData(EnvironmentState.Requested)]
    [InlineData(EnvironmentState.Provisioning)]
    [InlineData(EnvironmentState.Preparing)]
    public void AllCreationStagesHoldConcurrentSlot(EnvironmentState state)
    {
        Assert.Equal("OwnerProvisioningExceeded", Decide([Entry(state)]).RejectionCode);
        Assert.Equal("PoolProvisioningExceeded", Decide([Entry(state, owner: Guid.NewGuid()),
            Entry(state, owner: Guid.NewGuid())]).RejectionCode);
    }

    [Theory]
    [InlineData(EnvironmentState.Failed)]
    [InlineData(EnvironmentState.NeedsAttention)]
    [InlineData(EnvironmentState.Expired)]
    public void UnknownOperationRetainsConcurrentSlotEvenAfterLifecycleFailure(EnvironmentState state)
    {
        Assert.Equal("OwnerProvisioningExceeded", Decide([Entry(state, unsettled: true)]).RejectionCode);
        Assert.Equal("PoolProvisioningExceeded", Decide([Entry(state, owner: Guid.NewGuid(), unsettled: true)],
            budget: Budget with { MaxConcurrentProvisioning = 1 }).RejectionCode);
        Assert.True(Decide([Entry(state)]).Allowed);
    }

    [Fact]
    public void OwnerQuotaSpansPoolsAndPoolCapacitySpansOwners()
    {
        Assert.Equal("OwnerStorageExceeded", Decide([Entry(EnvironmentState.Ready, 950, pool: Guid.NewGuid())]).RejectionCode);
        Assert.Equal("PoolCapacityExceeded", Decide([Entry(EnvironmentState.Failed, 1950, owner: Guid.NewGuid())]).RejectionCode);
    }

    [Fact]
    public void PoolWithoutExplicitPositiveBudgetIsDisabled()
    {
        Assert.Equal("PoolDisabledOrUnbudgeted", Decide([], budget: Budget with { CapacityBytes = 0 }).RejectionCode);
        Assert.Equal("PoolDisabledOrUnbudgeted", Decide([], budget: Budget with { Enabled = false }).RejectionCode);
        Assert.Equal("EnvironmentSizeExceeded", Decide([], budget: Budget with { MaxEnvironmentBytes = 99 }).RejectionCode);
        Assert.Equal("InvalidQuotaConfiguration", Decide([], limits: Limits with { MaxStorageBytes = 0 }).RejectionCode);
    }

    [Fact]
    public void StorageBoundaryAndLargeNumbersDoNotOverflow()
    {
        Assert.True(Decide([Entry(EnvironmentState.Ready, 900)]).Allowed);
        Assert.Equal("OwnerStorageExceeded", Decide([Entry(EnvironmentState.Ready, long.MaxValue)],
            Limits with { MaxStorageBytes = long.MaxValue }).RejectionCode);
    }

    [Fact]
    public void InvalidOrDuplicateSnapshotsCannotSilentlyUndercount()
    {
        var row = Entry(EnvironmentState.Ready);
        Assert.Equal("InvalidSnapshot", Assert.Throws<DomainRuleException>(() => Decide([row, row])).Code);
        Assert.Equal("InvalidSnapshot", Assert.Throws<DomainRuleException>(() => Decide([row with { ReservedBytes = 0 }])).Code);
        Assert.Equal("InvalidSnapshot", Assert.Throws<DomainRuleException>(() =>
            Decide([Entry(EnvironmentState.Deleted, unsettled: true)])).Code);
    }

    [Fact]
    public void InitialTtlIsBoundedByPolicy()
    {
        var decision = EnvironmentQuotaPolicy.CanReserve(Proposed(), TimeSpan.FromDays(8), Limits, Budget, []);
        Assert.Equal("TtlExceeded", decision.RejectionCode);
    }
}
