using BuildSystem.Agent.Execution;
using Xunit;

namespace BuildSystem.PipelineTests;

public sealed class ScheduledJobsReadbackSequenceTests
{
    private const string FailureCode = "InvalidScheduledJobsReadbackSequence";

    private static ScheduledJobsReadbackScope[] Scopes()
    {
        var before = new ScheduledJobsReadbackScope(Guid.NewGuid(), 7, Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ScheduledJobsCheckpoint.BeforeRestore);
        return [before,
            before with { ObservationId = Guid.NewGuid(), Checkpoint = ScheduledJobsCheckpoint.AfterRestore },
            before with { ServiceEpoch = Guid.NewGuid(), ObservationId = Guid.NewGuid(),
                Checkpoint = ScheduledJobsCheckpoint.AfterServiceRestart }];
    }

    private static string Output(ScheduledJobsReadbackScope scope, string deny = "on") =>
        $"infobase : {scope.InfobaseId:D}\nscheduled-jobs-deny : {deny}\n";

    private static ScheduledJobsBlockedReadback Parse(ScheduledJobsReadbackScope scope) =>
        ScheduledJobsReadback.Parse(scope, 0, false, Output(scope));

    private static ScheduledJobsBlockedReadback[] ParseAll(ScheduledJobsReadbackScope[] scopes) =>
        scopes.Select(Parse).ToArray();

    private static void Require(ScheduledJobsBlockedReadback?[] readbacks) =>
        ScheduledJobsReadbackSequence.RequireComplete(readbacks[0], readbacks[1], readbacks[2]);

    private static void Reject(ScheduledJobsBlockedReadback?[] readbacks)
    {
        var error = Assert.Throws<ScheduledJobsReadbackException>(() => Require(readbacks));
        Assert.Equal(FailureCode, error.Code);
        Assert.Equal(FailureCode, error.Message);
        foreach (var readback in readbacks)
        {
            if (readback is null) continue;
            var scope = readback.Scope;
            foreach (var pin in new[] { scope.OperationId, scope.TargetIncarnationId, scope.ClusterId,
                scope.InfobaseId, scope.ServiceEpoch, scope.ObservationId })
                Assert.DoesNotContain(pin.ToString(), error.ToString());
        }
    }

    [Fact]
    public void Accepts_complete_correlated_triplet_without_consuming_or_mutating_inputs()
    {
        var scopes = Scopes();
        var snapshots = scopes.Select(scope => scope with { }).ToArray();
        var readbacks = ParseAll(scopes);
        Require(readbacks);
        Require(readbacks); // Correlation is not a replay guard or a consumption mechanism.
        for (var position = 0; position < 3; position++)
        {
            Assert.Same(scopes[position], readbacks[position].Scope);
            Assert.Equal(snapshots[position], readbacks[position].Scope);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Rejects_every_common_pin_mismatch_at_each_position(int position)
    {
        var scopes = Scopes();
        var scope = scopes[position];
        foreach (var changed in new[]
        {
            scope with { OperationId = Guid.NewGuid() },
            scope with { FencingToken = scope.FencingToken + 1 },
            scope with { TargetIncarnationId = Guid.NewGuid() },
            scope with { ClusterId = Guid.NewGuid() },
            scope with { InfobaseId = Guid.NewGuid() }
        })
        {
            var candidate = (ScheduledJobsReadbackScope[])scopes.Clone();
            candidate[position] = changed;
            var readbacks = ParseAll(candidate);
            Reject(readbacks);
            for (var index = 0; index < 3; index++)
                Assert.Same(candidate[index], readbacks[index].Scope);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Rejects_missing_readback_at_each_position(int position)
    {
        ScheduledJobsBlockedReadback?[] readbacks = ParseAll(Scopes());
        readbacks[position] = null;
        Reject(readbacks);
    }

    [Fact]
    public void Rejects_all_missing_readbacks() => Reject([null, null, null]);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Missing_or_off_parser_output_cannot_supply_a_required_readback(int position)
    {
        var scopes = Scopes();
        foreach (var output in new[] { $"infobase : {scopes[position].InfobaseId:D}\n", Output(scopes[position], "off") })
        {
            ScheduledJobsBlockedReadback?[] readbacks = ParseAll(scopes);
            readbacks[position] = null;
            Assert.Throws<ScheduledJobsReadbackException>(() =>
                readbacks[position] = ScheduledJobsReadback.Parse(scopes[position], 0, false, output));
            Reject(readbacks);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Requires_exact_checkpoint_label_at_each_position(int position)
    {
        var scopes = Scopes();
        foreach (var checkpoint in Enum.GetValues<ScheduledJobsCheckpoint>())
        {
            if (checkpoint == scopes[position].Checkpoint) continue;
            var candidate = (ScheduledJobsReadbackScope[])scopes.Clone();
            candidate[position] = candidate[position] with { Checkpoint = checkpoint };
            Reject(ParseAll(candidate));
        }
    }

    [Theory]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    public void Rejects_every_nonidentity_order(int first, int second, int third)
    {
        var readbacks = ParseAll(Scopes());
        Reject([readbacks[first], readbacks[second], readbacks[third]]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    public void Rejects_reused_observation_id_in_every_pair(int first, int second)
    {
        var scopes = Scopes();
        scopes[second] = scopes[second] with { ObservationId = scopes[first].ObservationId };
        Reject(ParseAll(scopes));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    public void Rejects_reused_readback_in_every_pair(int first, int second)
    {
        var readbacks = ParseAll(Scopes());
        readbacks[second] = readbacks[first];
        Reject(readbacks);
    }

    [Fact]
    public void Rejects_epoch_changes_before_restart_and_unchanged_restart_epoch()
    {
        var scopes = Scopes();
        var beforeChanged = (ScheduledJobsReadbackScope[])scopes.Clone();
        beforeChanged[0] = beforeChanged[0] with { ServiceEpoch = Guid.NewGuid() };
        Reject(ParseAll(beforeChanged));
        var afterChanged = (ScheduledJobsReadbackScope[])scopes.Clone();
        afterChanged[1] = afterChanged[1] with { ServiceEpoch = Guid.NewGuid() };
        Reject(ParseAll(afterChanged));
        var afterMatchesRestart = (ScheduledJobsReadbackScope[])scopes.Clone();
        afterMatchesRestart[1] = afterMatchesRestart[1] with { ServiceEpoch = scopes[2].ServiceEpoch };
        Reject(ParseAll(afterMatchesRestart));
        scopes[2] = scopes[2] with { ServiceEpoch = scopes[0].ServiceEpoch };
        Reject(ParseAll(scopes));
    }
}
