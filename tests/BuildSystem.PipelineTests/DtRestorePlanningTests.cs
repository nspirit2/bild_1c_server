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
    public void Fixed_recipe_vector_matches_independently_computed_sha256()
    {
        var request = new DtRestoreReviewRequest(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 42,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            123456789, "dt", "profile-v1");
        // Independently computed with Python hashlib.sha256 over UTF-8 json.dumps(...,
        // separators=(',', ':')), not with this planner or System.Text.Json. Field order:
        // RecipeVersion, EnvironmentId, JobId, FencingToken, ReservationId, ArtifactId,
        // ArchiveVersionId, TargetId, ClusterId, SqlServerId, Sha256 (uppercase), SizeBytes,
        // Format, PreparationProfileVersion, InfobaseName, SqlDatabaseName. GUIDs are lowercase
        // D strings; both names are bs_test_33333333333333333333333333333333; no trailing newline.
        const string expected = "12599205F3A9D1879FA730DEDB798DA3E4804427DED97BC5DC1F6A0D8558B5DA";
        Assert.Equal(expected, Planner(request).Plan(request).RecipeSha256);
    }
    [Fact]
    public void Public_plan_surface_stays_read_only_and_has_no_execution_entrypoint()
    {
        var type = typeof(ReviewOnlyDtRestorePlan);
        const System.Reflection.BindingFlags declaredPublic = System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.DeclaredOnly;
        Assert.True(type.IsSealed);
        Assert.Empty(type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
        var properties = type.GetProperties(declaredPublic);
        Assert.Equal(new[] { "RecipeSha256", "RecipeVersion", "Request", "RequiredPhases", "ReviewOnly", "Target" },
            properties.Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(properties, property =>
        {
            Assert.NotNull(property.GetMethod);
            Assert.Null(property.SetMethod);
        });
        // Do not include inherited Object methods. Even an innocuously named new execution
        // converter must cause this exact method allowlist to fail review.
        Assert.Equal(new[] { "ToString" }, type.GetMethods(declaredPublic)
            .Where(method => !method.IsSpecialName).Select(method => method.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(type.GetFields(declaredPublic));
        Assert.Empty(type.GetEvents(declaredPublic));
    }
    [Fact]
    public void Recipe_identity_is_independent_of_current_and_ui_culture()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        var originalUiCulture = System.Globalization.CultureInfo.CurrentUICulture;
        var request = Request() with { PreparationProfileVersion = "ISOLATION-I-v1", Sha256 = new string('a', 64) };
        var planner = Planner(request);
        try
        {
            // CurrentCulture flows through ExecutionContext; never mutate DefaultThreadCurrentCulture.
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
            var invariantHash = planner.Plan(request).RecipeSha256;
            var turkish = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            System.Globalization.CultureInfo.CurrentCulture = turkish;
            System.Globalization.CultureInfo.CurrentUICulture = turkish;
            Assert.Equal(invariantHash, planner.Plan(request).RecipeSha256);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
            System.Globalization.CultureInfo.CurrentUICulture = originalUiCulture;
        }
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
