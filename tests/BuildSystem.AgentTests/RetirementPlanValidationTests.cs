using System.Security.Cryptography;
using System.Text;
using BuildSystem.Agent;
using BuildSystem.Contracts;
using Xunit;

namespace BuildSystem.AgentTests;

public sealed class RetirementPlanValidationTests
{
    private static StageRetirementPlan Plan(string format = "dt")
    {
        var reservation = Guid.NewGuid();
        var version = Guid.NewGuid();
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes($"archive-stage-v1:{reservation:N}:{version:N}"));
        identity[6] = (byte)((identity[6] & 0x0f) | 0x80);
        identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
        return new StageRetirementPlan(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new Guid(identity.AsSpan(0, 16), bigEndian: true), reservation, version, 1, new string('A', 64), 4096, format);
    }

    [Theory]
    [InlineData("dt")]
    [InlineData("bak")]
    public void DeterministicUuidV8PlanIsValidForBothFormats(string format)
    {
        var plan = Plan(format);
        RetirementPlanValidation.Validate(plan);
        Assert.Equal(8, (plan.ArtifactId.ToByteArray(bigEndian: true)[6] >> 4));
    }

    [Fact]
    public void IndependentlyComputedUuidV8VectorIsAccepted()
    {
        var plan = Plan() with
        {
            ReservationId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ArchiveVersionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ArtifactId = Guid.Parse("9a758778-4649-8265-962c-69ac9e10799f"),
            Sha256 = string.Concat(Enumerable.Repeat("0123456789aBcDeF", 4))
        };
        RetirementPlanValidation.Validate(plan);
    }

    [Fact]
    public void DifferentArtifactIdIsRejected() => Invalid(Plan() with { ArtifactId = Guid.NewGuid() });

    [Theory]
    [InlineData("RetirementId")]
    [InlineData("EnvironmentId")]
    [InlineData("JobId")]
    [InlineData("ArtifactId")]
    [InlineData("ReservationId")]
    [InlineData("ArchiveVersionId")]
    public void EmptyIdentityIsRejected(string property)
    {
        var plan = Plan();
        plan = property switch
        {
            "RetirementId" => plan with { RetirementId = Guid.Empty },
            "EnvironmentId" => plan with { EnvironmentId = Guid.Empty },
            "JobId" => plan with { JobId = Guid.Empty },
            "ArtifactId" => plan with { ArtifactId = Guid.Empty },
            "ReservationId" => plan with { ReservationId = Guid.Empty },
            "ArchiveVersionId" => plan with { ArchiveVersionId = Guid.Empty },
            _ => throw new InvalidOperationException()
        };
        Invalid(plan);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveFenceIsRejected(long fence) => Invalid(Plan() with { FencingToken = fence });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveSizeIsRejected(long size) => Invalid(Plan() with { SizeBytes = size });

    [Theory]
    [InlineData("DT")]
    [InlineData("Bak")]
    [InlineData("zip")]
    [InlineData("")]
    public void UnrecognizedFormatIsRejected(string format) => Invalid(Plan() with { Format = format });

    [Fact]
    public void WrongHashLengthIsRejected() => Invalid(Plan() with { Sha256 = new string('A', 63) });

    [Fact]
    public void NonHexHashIsRejected() => Invalid(Plan() with { Sha256 = new string('G', 64) });

    [Fact]
    public void NonAsciiHexLookalikeIsRejected() => Invalid(Plan() with { Sha256 = new string('Ａ', 64) });

    [Fact]
    public void NullPlanIsRejected() => Assert.Throws<ArgumentException>(() => RetirementPlanValidation.Validate(null!));

    private static void Invalid(StageRetirementPlan plan) =>
        Assert.Throws<ArgumentException>(() => RetirementPlanValidation.Validate(plan));
}
// end
