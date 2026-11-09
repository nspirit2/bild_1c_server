namespace BuildSystem.Agent.Tools;

/// <summary>A RAC acknowledgement is only CLI output, never proof of ownership or target readiness.</summary>
public enum RacInfobaseCreationOutcomeKind
{
    AcknowledgementObserved,
    ReconciliationRequired
}

/// <summary>Fixed-code error without scope, output, or process details.</summary>
public sealed class RacInfobaseCreationOutcomeException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Inert interpretation of one attempted invocation. Even a valid acknowledgement requires
/// independent authoritative verification before any target can be used or unblocked.
/// </summary>
public sealed class RacInfobaseCreationOutcome
{
    internal RacInfobaseCreationOutcome(RacInfobaseCreationScope scope,
        RacInfobaseCreationOutcomeKind kind, Guid? reportedInfobaseId, string reasonCode)
    {
        Scope = scope;
        Kind = kind;
        ReportedInfobaseId = reportedInfobaseId;
        ReasonCode = reasonCode;
    }

    public RacInfobaseCreationScope Scope { get; }
    public RacInfobaseCreationOutcomeKind Kind { get; }
    public Guid? ReportedInfobaseId { get; }
    public string ReasonCode { get; }
    public bool Executable => false;
    public bool ScheduledJobsMustRemainBlocked => true;
    public bool RequiresIndependentVerification => true;
    public bool RetryAllowed => false;
    public override string ToString() => "RacInfobaseCreationOutcome [details omitted]";
}

/// <summary>
/// Pure, fail-closed parser. Neither RAC output nor a successful process exit supplies an
/// authorization, trusted mapping, existence check, or permission to retry an uncertain attempt.
/// </summary>
public static class RacInfobaseCreationOutcomes
{
    public const int MaxOutputCharacters = 4096;

    public static RacInfobaseCreationOutcome Classify(RacInfobaseCreationScope scope,
        ProcessResult? result, string? standardOutput, string? standardError,
        bool captureFailed = false, bool cancellationObserved = false)
    {
        // Check all correlation pins before inspecting process or captured output.
        if (scope is null || scope.OperationId == Guid.Empty || scope.PermitId == Guid.Empty ||
            scope.AttemptId == Guid.Empty || scope.ReservationId == Guid.Empty ||
            scope.TargetIncarnationId == Guid.Empty || scope.CanonicalClusterId == Guid.Empty ||
            scope.CanonicalSqlServerId == Guid.Empty || scope.EndpointMappingId == Guid.Empty)
            throw new RacInfobaseCreationOutcomeException("InvalidRacInfobaseCreationScope");

        RacInfobaseCreationOutcome Unknown() => new(scope,
            RacInfobaseCreationOutcomeKind.ReconciliationRequired, null, "OutcomeUnknown");

        // Every possible invocation needs reconciliation unless the entire capture is unambiguous.
        if (captureFailed || cancellationObserved || result is null ||
            result.Completion != ProcessCompletion.Exited || result.ExitCode != 0 ||
            result.ProcessId is null or <= 0 || result.OutputTruncated ||
            standardOutput is null || standardOutput.Length > MaxOutputCharacters ||
            standardError is null || standardError.Length != 0 ||
            standardOutput.Contains("[REDACTED]", StringComparison.OrdinalIgnoreCase))
            return Unknown();

        // Accept LF or CRLF only, and no other controls (including a bare CR or NUL).
        for (var i = 0; i < standardOutput.Length; i++)
        {
            var c = standardOutput[i];
            if (c == '\r' && (i + 1 == standardOutput.Length || standardOutput[i + 1] != '\n'))
                return Unknown();
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
                return Unknown();
        }

        var lines = standardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? acknowledgement = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim(' ', '\t');
            if (line.Length == 0)
                continue;
            if (acknowledgement is not null)
                return Unknown();
            acknowledgement = line;
        }

        if (acknowledgement is null)
            return Unknown();
        var colon = acknowledgement.IndexOf(':');
        if (colon < 0 || acknowledgement[..colon].Trim(' ', '\t') != "infobase")
            return Unknown();
        var value = acknowledgement[(colon + 1)..].Trim(' ', '\t');
        if (value.Length != 36 || !Guid.TryParseExact(value, "D", out var id) ||
            id == Guid.Empty || !string.Equals(value, id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            return Unknown();

        return new RacInfobaseCreationOutcome(scope,
            RacInfobaseCreationOutcomeKind.AcknowledgementObserved, id,
            "CreationAcknowledgementObserved");
    }
}
