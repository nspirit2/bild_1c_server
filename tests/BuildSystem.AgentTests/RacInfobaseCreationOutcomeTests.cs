using System.Reflection;
using BuildSystem.Agent.Tools;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class RacInfobaseCreationOutcomeTests
{
    private const string InfobaseId = "abcdef12-3456-4789-abcd-1234567890ab";
    private const string Acknowledgement = "infobase : " + InfobaseId;

    private static RacInfobaseCreationScope Scope() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private static ProcessResult Success() => new(123, 0, ProcessCompletion.Exited, false);

    private static void AssertSafety(RacInfobaseCreationOutcome outcome, RacInfobaseCreationScope scope)
    {
        Assert.Equal(scope, outcome.Scope);
        Assert.False(outcome.Executable);
        Assert.True(outcome.ScheduledJobsMustRemainBlocked);
        Assert.True(outcome.RequiresIndependentVerification);
        Assert.False(outcome.RetryAllowed);
    }

    private static RacInfobaseCreationOutcome Unknown(string? stdout, string? stderr = "",
        ProcessResult? result = null, bool captureFailed = false, bool cancellationObserved = false)
    {
        var scope = Scope();
        var outcome = RacInfobaseCreationOutcomes.Classify(scope, result ?? Success(), stdout, stderr,
            captureFailed, cancellationObserved);
        AssertSafety(outcome, scope);
        Assert.Equal(RacInfobaseCreationOutcomeKind.ReconciliationRequired, outcome.Kind);
        Assert.Null(outcome.ReportedInfobaseId);
        Assert.Equal("OutcomeUnknown", outcome.ReasonCode);
        return outcome;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EveryScopeIdentifierIsValidatedBeforeUntrustedOutcomeData(int index)
    {
        var scope = Scope();
        var invalid = index switch
        {
            0 => scope with { OperationId = Guid.Empty },
            1 => scope with { PermitId = Guid.Empty },
            2 => scope with { AttemptId = Guid.Empty },
            3 => scope with { ReservationId = Guid.Empty },
            4 => scope with { TargetIncarnationId = Guid.Empty },
            5 => scope with { CanonicalClusterId = Guid.Empty },
            6 => scope with { CanonicalSqlServerId = Guid.Empty },
            _ => scope with { EndpointMappingId = Guid.Empty }
        };
        var error = Assert.Throws<RacInfobaseCreationOutcomeException>(() =>
            RacInfobaseCreationOutcomes.Classify(invalid, null, new string('x', 4097), null, true, true));
        Assert.Equal("InvalidRacInfobaseCreationScope", error.Code);
    }

    [Fact]
    public void NullScopeIsRejectedBeforeMissingProcessAndOutput()
    {
        var error = Assert.Throws<RacInfobaseCreationOutcomeException>(() =>
            RacInfobaseCreationOutcomes.Classify(null!, null, null, null, true, true));
        Assert.Equal("InvalidRacInfobaseCreationScope", error.Code);
    }

    [Theory]
    [InlineData("infobase : " + InfobaseId)]
    [InlineData("infobase:" + InfobaseId + "\n")]
    [InlineData(" \tinfobase\t :\t " + InfobaseId + " \t")]
    [InlineData("\n \t\ninfobase : " + InfobaseId + "\n\t\n")]
    [InlineData("\r\n \t\r\ninfobase : " + InfobaseId + "\r\n\r\n")]
    [InlineData("infobase : ABCDEF12-3456-4789-ABCD-1234567890AB")]
    public void ExactAcknowledgementReportsOnlyParsedIdentifierAndNeverReadiness(string output)
    {
        var scope = Scope();
        var outcome = RacInfobaseCreationOutcomes.Classify(scope, Success(), output, "");
        AssertSafety(outcome, scope);
        Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, outcome.Kind);
        Assert.Equal(Guid.Parse(InfobaseId), outcome.ReportedInfobaseId);
        Assert.Equal("CreationAcknowledgementObserved", outcome.ReasonCode);
    }

    [Fact]
    public void OutputLimitIsPublicAndInclusive()
    {
        Assert.Equal(4096, RacInfobaseCreationOutcomes.MaxOutputCharacters);
        var scope = Scope();
        var boundary = Acknowledgement + new string(' ', 4096 - Acknowledgement.Length);
        var outcome = RacInfobaseCreationOutcomes.Classify(scope, Success(), boundary, "");
        Assert.Equal(RacInfobaseCreationOutcomeKind.AcknowledgementObserved, outcome.Kind);
        Assert.Equal(Guid.Parse(InfobaseId), outcome.ReportedInfobaseId);
        AssertSafety(outcome, scope);
        Unknown(boundary + " ");
    }

    [Fact]
    public void MissingResultCannotAcknowledgeOtherwiseValidOutput()
    {
        var scope = Scope();
        var outcome = RacInfobaseCreationOutcomes.Classify(scope, null, Acknowledgement, "");
        AssertSafety(outcome, scope);
        Assert.Equal(RacInfobaseCreationOutcomeKind.ReconciliationRequired, outcome.Kind);
        Assert.Null(outcome.ReportedInfobaseId);
        Assert.Equal("OutcomeUnknown", outcome.ReasonCode);
    }

    public static IEnumerable<object[]> UnsuccessfulResults()
    {
        yield return [new ProcessResult(null, 0, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(0, 0, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(-1, 0, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(123, null, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(123, 1, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(123, -1, ProcessCompletion.Exited, false)];
        yield return [new ProcessResult(123, 0, ProcessCompletion.TimedOut, false)];
        yield return [new ProcessResult(123, 0, ProcessCompletion.Cancelled, false)];
        yield return [new ProcessResult(123, 0, (ProcessCompletion)999, false)];
        yield return [new ProcessResult(123, 0, ProcessCompletion.Exited, true)];
    }

    [Theory]
    [MemberData(nameof(UnsuccessfulResults))]
    public void UnsuccessfulProcessMetadataCannotBeOverriddenByValidOutput(ProcessResult result) =>
        Unknown(Acknowledgement, result: result);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CaptureFailureOrObservedCancellationRequiresReconciliation(bool captureFailed, bool cancellationObserved) =>
        Unknown(Acknowledgement, captureFailed: captureFailed, cancellationObserved: cancellationObserved);

    [Fact]
    public void MissingOrAnyNonemptyStderrRequiresReconciliation()
    {
        foreach (var stderr in new string?[] { null, " ", "\t", "\n", "\r\n", "warning", Acknowledgement, "[REDACTED]", new string('x', 4097) })
            Unknown(Acknowledgement, stderr);
    }

    [Fact]
    public void MissingEmptyOrBlankOutputRequiresReconciliation()
    {
        foreach (var stdout in new string?[] { null, "", " ", "\t", "\n", "\r\n", " \t\n\r\n" })
            Unknown(stdout);
    }

    [Fact]
    public void OnlyCanonicalNonemptyDFormatIdentifierIsAccepted()
    {
        var id = Guid.Parse(InfobaseId);
        foreach (var value in new[] { id.ToString("N"), id.ToString("B"), id.ToString("P"), id.ToString("X"),
            Guid.Empty.ToString("D"), "not-a-guid", InfobaseId[..^1], InfobaseId + "0", InfobaseId.Replace('a', 'g') })
            Unknown("infobase : " + value);
    }

    [Fact]
    public void UnknownFieldsHeadersRawIdentifiersAndDuplicateAcknowledgementsAreUnknown()
    {
        foreach (var output in new[]
        {
            InfobaseId, "Infobase : " + InfobaseId, "INFOBASE : " + InfobaseId,
            "uuid : " + InfobaseId, "infobase-id : " + InfobaseId,
            "infobase " + InfobaseId, "infobase :: " + InfobaseId,
            "infobase : ", "rac infobase create\n" + Acknowledgement,
            Acknowledgement + "\nname : private-name", "name : private-name\n" + Acknowledgement,
            Acknowledgement + "\n" + Acknowledgement,
            Acknowledgement + "\ninfobase : 12345678-1234-4123-8123-123456789012",
            Acknowledgement + " " + InfobaseId, Acknowledgement + " trailing"
        })
            Unknown(output);
    }

    [Fact]
    public void BareCarriageReturnsAndNonAsciiWhitespaceNeverCountAsLineOrFieldWhitespace()
    {
        foreach (var output in new[]
        {
            "\r" + Acknowledgement, Acknowledgement + "\r", "\r" + Acknowledgement + "\r",
            Acknowledgement + "\r\n\r", "infobase\r : " + InfobaseId,
            "\u00a0" + Acknowledgement, Acknowledgement + "\u00a0", "infobase\u00a0: " + InfobaseId,
            "infobase :\u00a0" + InfobaseId, "\u2028" + Acknowledgement, Acknowledgement + "\u2029"
        })
            Unknown(output);
    }

    [Fact]
    public void ControlCharacterMutationsCannotProduceAcknowledgement()
    {
        foreach (var control in new[] { '\0', '\u0001', '\u0007', '\b', '\v', '\f', '\u001b', '\u001f', '\u007f', '\u0085' })
        {
            Unknown(control + Acknowledgement);
            Unknown("infobase : " + InfobaseId[..8] + control + InfobaseId[8..]);
            Unknown(Acknowledgement + control);
        }
    }

    [Fact]
    public void RedactionMarkersAnywhereInOutputRequireReconciliation()
    {
        foreach (var output in new[] { "[REDACTED]", "[REDACTED]\n" + Acknowledgement,
            Acknowledgement + "\n[REDACTED]", "infobase : [REDACTED]", Acknowledgement + "[REDACTED]" })
            Unknown(output);
    }

    [Fact]
    public void DiagnosticsDoNotExposeParsedIdentifierOrUntrustedOutputCredentials()
    {
        var acknowledged = RacInfobaseCreationOutcomes.Classify(Scope(), Success(), Acknowledgement, "");
        Assert.DoesNotContain(InfobaseId, acknowledged.ToString(), StringComparison.OrdinalIgnoreCase);
        const string secret = "private-db-password-never-log";
        var unknown = Unknown(Acknowledgement + "\n--db-pwd=" + secret, "--cluster-pwd=" + secret);
        Assert.DoesNotContain(secret, unknown.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(InfobaseId, unknown.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OutcomeDoesNotRetainRawStreamsOrProcessResult()
    {
        const string secret = "untrusted-output-secret-never-retain";
        var outcome = Unknown(secret, secret);
        var fields = typeof(RacInfobaseCreationOutcome).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var field in fields)
        {
            Assert.False(typeof(ProcessResult).IsAssignableFrom(field.FieldType));
            if (field.GetValue(outcome) is string value)
                Assert.DoesNotContain(secret, value, StringComparison.Ordinal);
        }
    }
}
