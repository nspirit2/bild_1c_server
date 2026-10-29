using BuildSystem.Agent.Execution;
using Xunit;

namespace BuildSystem.PipelineTests;

public sealed class DtRestorePlanningTests
{
    private static DtRestoreReviewRequest Request() => new(Guid.NewGuid(), Guid.NewGuid(), 7,
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new string('a', 64), 1234, "dt", "isolation-v1");
    private static DtRestoreTarget Target(DtRestoreReviewRequest r) => new(r.TargetId, r.ClusterId, r.SqlServerId,
        "bs_test_" + r.ReservationId.ToString("N"), "bs_test_" + r.ReservationId.ToString("N"));
    private static DtRestoreReviewPlanner Planner(DtRestoreReviewRequest r) => new([Target(r)]);
    private static string Error(DtRestoreReviewRequest r, DtRestoreReviewPlanner? planner = null) =>
        Assert.Throws<DtRestorePlanningException>(() => (planner ?? Planner(r)).Plan(r)).Code;

    [Fact]
    public void Review_descriptor_has_fixed_order_without_claiming_checks_succeeded()
    {
        var r = Request(); var plan = Planner(r).Plan(r);
        Assert.True(plan.ReviewOnly); Assert.Equal("dt-restore-review-v1", plan.RecipeVersion);
        Assert.Equal(new[] { DtRestoreReviewPhase.VerifyArtifact, DtRestoreReviewPhase.VerifyFreshAuthority,
            DtRestoreReviewPhase.VerifyOwnedEmptyTarget, DtRestoreReviewPhase.VerifyScheduledJobsBlocked,
            DtRestoreReviewPhase.RestoreDt, DtRestoreReviewPhase.PrepareIsolation,
            DtRestoreReviewPhase.Verify, DtRestoreReviewPhase.Publish }, plan.RequiredPhases);
        Assert.Equal(r with { Sha256 = r.Sha256.ToUpperInvariant() }, plan.Request);
    }
    [Fact]
    public void Same_pins_have_stable_hash_and_hex_case_is_normalized()
    {
        var r = Request(); var planner = Planner(r);
        Assert.Equal(planner.Plan(r).RecipeSha256, planner.Plan(r with { Sha256 = r.Sha256.ToUpperInvariant() }).RecipeSha256);
        Assert.Equal(64, planner.Plan(r).RecipeSha256.Length);
    }
    [Fact]
    public void Every_request_pin_participates_in_plan_identity()
    {
        var r = Request(); var baseline = Planner(r).Plan(r).RecipeSha256;
        var alternatives = new[] { r with { EnvironmentId = Guid.NewGuid() }, r with { JobId = Guid.NewGuid() },
            r with { FencingToken = 8 }, r with { ReservationId = Guid.NewGuid() }, r with { ArtifactId = Guid.NewGuid() },
            r with { ArchiveVersionId = Guid.NewGuid() }, r with { TargetId = Guid.NewGuid() },
            r with { ClusterId = Guid.NewGuid() }, r with { SqlServerId = Guid.NewGuid() },
            r with { Sha256 = new string('B', 64) }, r with { SizeBytes = 1235 },
            r with { PreparationProfileVersion = "isolation-v2" } };
        foreach (var alternate in alternatives) Assert.NotEqual(baseline, Planner(alternate).Plan(alternate).RecipeSha256);
    }
    [Fact]
    public void Mutable_operator_collection_cannot_change_pinned_mapping()
    {
        var r = Request(); var source = new List<DtRestoreTarget> { Target(r) };
        var planner = new DtRestoreReviewPlanner(source); var before = planner.Plan(r);
        source.Clear(); source.Add(Target(r) with { ClusterId = Guid.NewGuid() });
        Assert.Equal(before.RecipeSha256, planner.Plan(r).RecipeSha256);
        var changedCopy = before.Target with { InfobaseName = "other" };
        Assert.NotEqual(changedCopy, planner.Plan(r).Target);
    }
    [Fact]
    public void Every_semantic_id_is_required()
    {
        var r = Request(); var planner = Planner(r);
        foreach (var invalid in new[] { r with { EnvironmentId = Guid.Empty }, r with { JobId = Guid.Empty },
            r with { ReservationId = Guid.Empty }, r with { ArtifactId = Guid.Empty }, r with { ArchiveVersionId = Guid.Empty },
            r with { TargetId = Guid.Empty }, r with { ClusterId = Guid.Empty }, r with { SqlServerId = Guid.Empty } })
            Assert.Equal("InvalidRestorePins", Error(invalid, planner));
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void Fence_and_size_must_be_positive(long n)
    {
        var r = Request(); Assert.Equal("InvalidRestorePins", Error(r with { FencingToken = n }));
        Assert.Equal("InvalidRestorePins", Error(r with { SizeBytes = n }));
    }
    [Theory]
    [InlineData("bak")] [InlineData("DT")] [InlineData("")] [InlineData(null)]
    public void Only_dt_is_in_this_recipe(string? format) => Assert.Equal("UnsupportedRestoreFormat", Error(Request() with { Format = format! }));
    [Theory]
    [InlineData("")] [InlineData("abc")] [InlineData(null)]
    public void Hash_must_be_exact_sha256_hex(string? hash) => Assert.Equal("InvalidRestorePins", Error(Request() with { Sha256 = hash! }));
    [Fact]
    public void Nonhex_hash_and_unbounded_profile_are_rejected()
    {
        Assert.Equal("InvalidRestorePins", Error(Request() with { Sha256 = new string('Z', 64) }));
        Assert.Equal("InvalidRestorePins", Error(Request() with { PreparationProfileVersion = new string('v', 129) }));
    }
    [Theory]
    [InlineData("")] [InlineData("profile with spaces")] [InlineData("x;flag")] [InlineData("../x")] [InlineData(null)]
    public void Profile_is_a_bounded_identifier(string? profile) => Assert.Equal("InvalidRestorePins", Error(Request() with { PreparationProfileVersion = profile! }));
    [Fact]
    public void Unknown_target_and_authority_mismatch_are_distinct_rejections()
    {
        var r = Request(); var planner = Planner(r);
        Assert.Equal("RestoreTargetUnavailable", Error(r with { TargetId = Guid.NewGuid() }, planner));
        Assert.Equal("RestoreTargetMismatch", Error(r with { ClusterId = Guid.NewGuid() }, planner));
        Assert.Equal("RestoreTargetMismatch", Error(r with { SqlServerId = Guid.NewGuid() }, planner));
    }
    [Fact]
    public void Names_must_match_exact_reservation_slot_not_merely_a_safe_prefix()
    {
        var r = Request(); var other = "bs_test_" + Guid.NewGuid().ToString("N");
        Assert.Equal("RestoreTargetMismatch", Error(r, new([Target(r) with { InfobaseName = other }])));
        Assert.Equal("RestoreTargetMismatch", Error(r, new([Target(r) with { SqlDatabaseName = other }])));
    }
    [Theory]
    [InlineData("production")] [InlineData("bs_test_x;DROP DATABASE x")] [InlineData("bs_test_x/arg")]
    [InlineData("bs_test_")] [InlineData(null)]
    public void Unsafe_or_noncanonical_operator_names_are_rejected(string? name)
    {
        var r = Request(); Assert.Equal("InvalidTargetMapping", Assert.Throws<DtRestorePlanningException>(
            () => new DtRestoreReviewPlanner([Target(r) with { InfobaseName = name! }])).Code);
    }
    [Fact]
    public void Empty_duplicate_or_invalid_operator_mappings_are_rejected()
    {
        var r = Request();
        foreach (var mappings in new[] { Array.Empty<DtRestoreTarget>(), new[] { Target(r), Target(r) },
            new[] { Target(r) with { TargetId = Guid.Empty } }, new[] { Target(r) with { ClusterId = Guid.Empty } },
            new[] { Target(r) with { SqlServerId = Guid.Empty } } })
            Assert.Equal("InvalidTargetMapping", Assert.Throws<DtRestorePlanningException>(() => new DtRestoreReviewPlanner(mappings)).Code);
        Assert.Equal("InvalidTargetMapping", Assert.Throws<DtRestorePlanningException>(() => new DtRestoreReviewPlanner(null!)).Code);
        Assert.Equal("InvalidRestorePins", Assert.Throws<DtRestorePlanningException>(() => Planner(r).Plan(null!)).Code);
    }
    [Fact]
    public void Operator_mapping_count_is_bounded_before_accepting_another_target()
    {
        var r = Request();
        var mappings = Enumerable.Range(0, DtRestoreReviewPlanner.MaxTargetMappings)
            .Select(_ => Target(r) with { TargetId = Guid.NewGuid() }).ToList();
        _ = new DtRestoreReviewPlanner(mappings);
        mappings.Add(Target(r) with { TargetId = Guid.NewGuid() });
        Assert.Equal("InvalidTargetMapping", Assert.Throws<DtRestorePlanningException>(
            () => new DtRestoreReviewPlanner(mappings)).Code);
    }
    [Fact]
    public void Diagnostics_do_not_echo_input_or_target_names()
    {
        var r = Request(); var plan = Planner(r).Plan(r);
        foreach (var text in new[] { r.ToString(), plan.Target.ToString(), plan.ToString() })
        {
            Assert.DoesNotContain(r.Sha256, text); Assert.DoesNotContain(plan.Target.InfobaseName, text);
            Assert.DoesNotContain(r.JobId.ToString(), text);
        }
        var error = Assert.Throws<DtRestorePlanningException>(() => Planner(r).Plan(r with { PreparationProfileVersion = "secret;value" }));
        Assert.Equal(error.Code, error.Message); Assert.DoesNotContain("secret", error.ToString());
    }
}
