namespace BuildSystem.Agent.Execution;

/// <summary>Readback checkpoints are distinct; a successful earlier check cannot replace a later one.</summary>
public enum ScheduledJobsCheckpoint { BeforeRestore, AfterRestore, AfterServiceRestart }

/// <summary>
/// Caller-supplied correlation only, not authority or ownership evidence. ServiceEpoch must change
/// after every restart; ObservationId must be new for every requested readback.
/// </summary>
public sealed record ScheduledJobsReadbackScope(Guid OperationId, long FencingToken,
    Guid TargetIncarnationId, Guid ClusterId, Guid InfobaseId, Guid ServiceEpoch,
    Guid ObservationId, ScheduledJobsCheckpoint Checkpoint)
{
    public override string ToString() => "ScheduledJobsReadbackScope [details omitted]";
}

public sealed class ScheduledJobsReadbackException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// A parsed statement from one RAC infobase info response, NOT a grant, live lock, freshness proof,
/// restore permit or Ready result. A trusted adapter must supply authentic, complete stdout and
/// correlate the requested cluster/operation/epoch; RAC infobase info does not report those pins.
/// The adapter must require normal process completion (not timeout/cancel), no read/callback failure,
/// and the pinned RAC executable/command. Exit code alone cannot establish these conditions.
/// </summary>
public sealed class ScheduledJobsBlockedReadback
{
    internal ScheduledJobsBlockedReadback(ScheduledJobsReadbackScope scope) => Scope = scope;
    public ScheduledJobsReadbackScope Scope { get; }
    public override string ToString() => "ScheduledJobsBlockedReadback [not authority; details omitted]";
}

/// <summary>Pure bounded parser. No IO, commands, mutation or registration.</summary>
public static class ScheduledJobsReadback
{
    public const int MaxOutputCharacters = 16384;

    public static ScheduledJobsBlockedReadback Parse(ScheduledJobsReadbackScope scope, int exitCode,
        bool outputTruncated, string stdout)
    {
        Validate(scope);
        if (exitCode != 0 || outputTruncated) throw Error("ScheduledJobsReadbackUnavailable");
        if (string.IsNullOrWhiteSpace(stdout) || stdout.Length > MaxOutputCharacters ||
            stdout.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw Error("InvalidScheduledJobsReadback");
        // Accept native Windows CRLF and LF only. A stray CR must not hide a second field.
        var normalized = stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains('\r')) throw Error("InvalidScheduledJobsReadback");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in normalized.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var colon = raw.IndexOf(':');
            if (colon < 1) throw Error("InvalidScheduledJobsReadback");
            var key = raw[..colon].Trim(' ', '\t');
            var value = raw[(colon + 1)..].Trim(' ', '\t');
            if (key.Length == 0 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                !fields.TryAdd(key, value)) throw Error("InvalidScheduledJobsReadback");
        }
        if (!fields.TryGetValue("infobase", out var id) ||
            !Guid.TryParseExact(id, "D", out var infobaseId) || infobaseId != scope.InfobaseId)
            throw Error("ScheduledJobsTargetMismatch");
        if (!fields.TryGetValue("scheduled-jobs-deny", out var deny))
            throw Error("InvalidScheduledJobsReadback");
        if (deny != "on") throw Error("ScheduledJobsNotBlocked");
        return new(scope);
    }

    /// <summary>Exact correlation only; does not prove elapsed-time freshness or prevent replay itself.</summary>
    public static void RequireMatch(ScheduledJobsBlockedReadback readback, ScheduledJobsReadbackScope expected)
    {
        Validate(expected);
        if (readback is null || readback.Scope != expected) throw Error("ScheduledJobsScopeMismatch");
    }

    private static void Validate(ScheduledJobsReadbackScope scope)
    {
        if (scope is null || scope.FencingToken <= 0 ||
            new[] { scope.OperationId, scope.TargetIncarnationId, scope.ClusterId, scope.InfobaseId,
                scope.ServiceEpoch, scope.ObservationId }.Any(x => x == Guid.Empty) ||
            !Enum.IsDefined(scope.Checkpoint)) throw Error("InvalidScheduledJobsScope");
    }

    private static ScheduledJobsReadbackException Error(string code) => new(code);
}
