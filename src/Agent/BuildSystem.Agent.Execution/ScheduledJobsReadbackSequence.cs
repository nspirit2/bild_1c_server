namespace BuildSystem.Agent.Execution;

/// <summary>
/// Pure verification of caller-supplied correlation only for three parsed blocked readbacks.
/// Requires the before-restore, after-restore and after-service-restart checkpoint labels in
/// that order, common operation/fence/incarnation/cluster/infobase pins, a shared pre-restart
/// service epoch, a different post-restart epoch and three distinct observation identifiers.
/// Does not establish elapsed-time freshness, actual chronology or proof of a service restart;
/// does not authenticate caller labels, prevent replay, grant authority or produce Ready.
/// </summary>
public static class ScheduledJobsReadbackSequence
{
    public static void RequireComplete(ScheduledJobsBlockedReadback? before,
        ScheduledJobsBlockedReadback? after, ScheduledJobsBlockedReadback? restart)
    {
        if (before is null || after is null || restart is null)
            throw Error();

        var b = before.Scope;
        var a = after.Scope;
        var r = restart.Scope;
        if (b.Checkpoint != ScheduledJobsCheckpoint.BeforeRestore ||
            a.Checkpoint != ScheduledJobsCheckpoint.AfterRestore ||
            r.Checkpoint != ScheduledJobsCheckpoint.AfterServiceRestart ||
            !SameCommonPins(b, a) || !SameCommonPins(b, r) ||
            b.ServiceEpoch != a.ServiceEpoch || b.ServiceEpoch == r.ServiceEpoch ||
            b.ObservationId == a.ObservationId || b.ObservationId == r.ObservationId ||
            a.ObservationId == r.ObservationId)
            throw Error();
    }

    private static bool SameCommonPins(ScheduledJobsReadbackScope left, ScheduledJobsReadbackScope right) =>
        left.OperationId == right.OperationId && left.FencingToken == right.FencingToken &&
        left.TargetIncarnationId == right.TargetIncarnationId && left.ClusterId == right.ClusterId &&
        left.InfobaseId == right.InfobaseId;

    private static ScheduledJobsReadbackException Error() => new("InvalidScheduledJobsReadbackSequence");
}
