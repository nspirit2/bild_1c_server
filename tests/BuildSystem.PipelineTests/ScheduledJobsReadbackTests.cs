using BuildSystem.Agent.Execution;
using Xunit;

namespace BuildSystem.PipelineTests;

public sealed class ScheduledJobsReadbackTests
{
    private static ScheduledJobsReadbackScope Scope() => new(Guid.NewGuid(), 7, Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ScheduledJobsCheckpoint.BeforeRestore);
    private static string Output(ScheduledJobsReadbackScope s) => $"infobase : {s.InfobaseId:D}\nscheduled-jobs-deny : on\nname : opaque\n";
    private static ScheduledJobsBlockedReadback Parse(ScheduledJobsReadbackScope s, string? text = null) =>
        ScheduledJobsReadback.Parse(s, 0, false, text ?? Output(s));

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Accepts_native_line_endings_and_field_order(string newline)
    {
        var s = Scope();
        var text = $"\t scheduled-jobs-deny\t : on\t{newline}name : \"opaque: value\"{newline}infobase : {s.InfobaseId:D}{newline}";
        var result = Parse(s, text);
        ScheduledJobsReadback.RequireMatch(result, s);
        Assert.Equal(s, result.Scope);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("ON")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData("on off")]
    public void Only_exact_on_is_accepted(string value)
    {
        var s = Scope();
        Assert.Equal("ScheduledJobsNotBlocked", Assert.Throws<ScheduledJobsReadbackException>(() =>
            Parse(s, $"infobase : {s.InfobaseId:D}\nscheduled-jobs-deny : {value}")).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("error without separator")]
    [InlineData("scheduled-jobs-deny : on\rscheduled-jobs-deny : off")]
    [InlineData("scheduled-jobs-deny : on\0")]
    public void Rejects_invalid_or_missing_output(string text)
    {
        Assert.Throws<ScheduledJobsReadbackException>(() => Parse(Scope(), text));
    }

    [Fact]
    public void Rejects_missing_duplicate_fields_and_wrong_target()
    {
        var s = Scope();
        foreach (var text in new[] { $"infobase : {s.InfobaseId:D}", "scheduled-jobs-deny : on",
            Output(s) + "scheduled-jobs-deny : on", Output(s) + "scheduled-jobs-deny : off",
            Output(s) + $"infobase : {s.InfobaseId:D}", Output(s) + "name : second",
            Output(s).Replace(s.InfobaseId.ToString(), Guid.NewGuid().ToString()),
            Output(s).Replace(s.InfobaseId.ToString(), "{" + s.InfobaseId + "}") })
            Assert.Throws<ScheduledJobsReadbackException>(() => Parse(s, text));
    }

    [Fact]
    public void Rejects_failed_truncated_and_oversized_output_even_if_fields_are_present()
    {
        var s = Scope();
        Assert.Throws<ScheduledJobsReadbackException>(() => ScheduledJobsReadback.Parse(s, 1, false, Output(s)));
        Assert.Throws<ScheduledJobsReadbackException>(() => ScheduledJobsReadback.Parse(s, 0, true, Output(s)));
        Assert.Throws<ScheduledJobsReadbackException>(() => Parse(s, Output(s) + new string('a', ScheduledJobsReadback.MaxOutputCharacters)));
    }

    [Fact]
    public void Every_scope_pin_must_match_including_restart_observation_and_checkpoint()
    {
        var s = Scope(); var readback = Parse(s);
        foreach (var changed in new[] { s with { OperationId = Guid.NewGuid() }, s with { FencingToken = 8 },
            s with { TargetIncarnationId = Guid.NewGuid() }, s with { ClusterId = Guid.NewGuid() },
            s with { InfobaseId = Guid.NewGuid() }, s with { ServiceEpoch = Guid.NewGuid() },
            s with { ObservationId = Guid.NewGuid() }, s with { Checkpoint = ScheduledJobsCheckpoint.AfterRestore },
            s with { Checkpoint = ScheduledJobsCheckpoint.AfterServiceRestart } })
            Assert.Equal("ScheduledJobsScopeMismatch", Assert.Throws<ScheduledJobsReadbackException>(() =>
                ScheduledJobsReadback.RequireMatch(readback, changed)).Code);
    }

    [Fact]
    public void Rejects_invalid_scope_and_null_readback()
    {
        var s = Scope();
        foreach (var invalid in new[] { s with { OperationId = Guid.Empty }, s with { FencingToken = 0 },
            s with { TargetIncarnationId = Guid.Empty }, s with { ClusterId = Guid.Empty },
            s with { InfobaseId = Guid.Empty }, s with { ServiceEpoch = Guid.Empty },
            s with { ObservationId = Guid.Empty }, s with { Checkpoint = (ScheduledJobsCheckpoint)99 } })
            Assert.Throws<ScheduledJobsReadbackException>(() => Parse(invalid));
        Assert.Throws<ScheduledJobsReadbackException>(() => ScheduledJobsReadback.RequireMatch(null!, s));
        Assert.Throws<ScheduledJobsReadbackException>(() => ScheduledJobsReadback.Parse(null!, 0, false, Output(s)));
    }

    [Theory]
    [InlineData(ScheduledJobsCheckpoint.AfterRestore)]
    [InlineData(ScheduledJobsCheckpoint.AfterServiceRestart)]
    public void Later_checkpoints_accept_their_own_readback(ScheduledJobsCheckpoint checkpoint)
    {
        var s = Scope() with { Checkpoint = checkpoint };
        ScheduledJobsReadback.RequireMatch(Parse(s), s);
    }

    [Fact]
    public void Parser_does_not_authenticate_caller_labels_or_prevent_same_scope_replay()
    {
        var s = Scope(); var text = Output(s); var first = Parse(s, text);
        var relabelled = s with { OperationId = Guid.NewGuid(), ClusterId = Guid.NewGuid(), ServiceEpoch = Guid.NewGuid() };
        ScheduledJobsReadback.RequireMatch(Parse(relabelled, text), relabelled);
        ScheduledJobsReadback.RequireMatch(first, s);
        ScheduledJobsReadback.RequireMatch(first, s); // Coordinator, not parser, must consume/invalidate observations.
    }

    [Fact]
    public void Rejects_null_and_corruption_even_with_otherwise_valid_fields()
    {
        var s = Scope();
        Assert.Throws<ScheduledJobsReadbackException>(() => ScheduledJobsReadback.Parse(s, 0, false, null!));
        foreach (var suffix in new[] { "\0", "\r", "\u001b", "\u007f" })
            Assert.Throws<ScheduledJobsReadbackException>(() => Parse(s, Output(s) + suffix));
        foreach (var id in new[] { "", "not-a-guid", Guid.Empty.ToString() })
            Assert.Throws<ScheduledJobsReadbackException>(() => Parse(s, Output(s).Replace(s.InfobaseId.ToString(), id)));
        Assert.Throws<ScheduledJobsReadbackException>(() => Parse(s with { FencingToken = -1 }));
    }

    [Fact]
    public void Diagnostics_and_surface_do_not_disclose_readback_or_create_authority()
    {
        var s = Scope();var result = Parse(s);
        Assert.DoesNotContain(s.InfobaseId.ToString(), s.ToString());
        Assert.DoesNotContain(s.InfobaseId.ToString(), result.ToString());
        Assert.Empty(typeof(ScheduledJobsBlockedReadback).GetConstructors());
        var ex = Assert.Throws<ScheduledJobsReadbackException>(() => Parse(s, "SECRET_ENDPOINT_AND_PASSWORD"));
        Assert.DoesNotContain("SECRET", ex.ToString());
    }
}
