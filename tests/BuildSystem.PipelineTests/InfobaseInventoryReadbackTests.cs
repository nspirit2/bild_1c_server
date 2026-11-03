using System.Collections;
using BuildSystem.Agent.Execution;
using Xunit;

namespace BuildSystem.PipelineTests;

public sealed class InfobaseInventoryReadbackTests
{
    private static InfobaseInventoryReadbackScope Scope()
    {
        var reservation = Guid.NewGuid();
        return new(Guid.NewGuid(), 7, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reservation,
            Guid.NewGuid(), $"bs_test_{reservation:N}");
    }
    private static string Record(string name, Guid? id = null, string? descr = null) =>
        $"infobase : {id ?? Guid.NewGuid():D}\nname : {name}\n" + (descr is null ? "" : $"descr : {descr}\n");
    private static InfobaseInventoryReadbackResult Parse(InfobaseInventoryReadbackScope s, string text) =>
        InfobaseInventoryReadback.Parse(s, 0, false, text);
    private static void Reject(string text) => Assert.Throws<InfobaseInventoryReadbackException>(() => Parse(Scope(), text));

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Accepts_native_endings_field_order_and_trailing_blanks(string newline)
    {
        var s = Scope(); var id = Guid.NewGuid();
        var result = Parse(s, $"\tname\t : {s.ExpectedInfobaseName}{newline}descr : opaque: value{newline}infobase : {id:D}{newline}{newline}{newline}");
        Assert.Equal(InfobaseInventoryObservation.Present, result.Observation);
        Assert.Equal(id, Assert.Single(result.Entries).InfobaseId);
        InfobaseInventoryReadback.RequireMatch(result, s);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\r\n \t\n")]
    public void Empty_complete_inventory_observes_absence_only(string text)
    {
        var result = Parse(Scope(), text);
        Assert.Empty(result.Entries);
        Assert.Equal(InfobaseInventoryObservation.Absent, result.Observation);
    }

    [Fact]
    public void Unrelated_names_do_not_match_target()
    {
        var s = Scope();
        Assert.Equal(InfobaseInventoryObservation.Absent, Parse(s, Record("other")).Observation);
        Assert.Equal(InfobaseInventoryObservation.Absent, Parse(s, Record(s.ExpectedInfobaseName + "suffix")).Observation);
    }

    [Fact]
    public void Case_only_target_alias_is_not_absence()
    {
        var s = Scope();
        Assert.Equal("AmbiguousInfobaseInventoryTarget", Assert.Throws<InfobaseInventoryReadbackException>(() =>
            Parse(s, Record(s.ExpectedInfobaseName.ToUpperInvariant()))).Code);
    }

    [Fact]
    public void Accepts_unicode_names_and_discards_optional_description()
    {
        var result = Parse(Scope(), Record("База_日本_😀:v", descr: "Описание: opaque") + "\n" + Record("other"));
        Assert.Equal("База_日本_😀:v", result.Entries[0].Name);
        Assert.Equal(2, result.Entries.Count);
        Assert.Null(typeof(InfobaseInventoryEntry).GetProperty("Description"));
    }

    [Theory]
    [InlineData("name : x\n")]
    [InlineData("infobase : 11111111-1111-1111-1111-111111111111\n")]
    [InlineData("name : \ninfobase : 11111111-1111-1111-1111-111111111111\n")]
    [InlineData("error without separator")]
    [InlineData(": value")]
    [InlineData("name = value")]
    public void Rejects_missing_fields_and_malformed_separators(string text) => Reject(text);

    [Theory]
    [InlineData("unknown : x")]
    [InlineData("Name : x")]
    [InlineData("name : second")]
    [InlineData("descr : one\ndescr : two")]
    [InlineData("infobase : 11111111-1111-1111-1111-111111111111")]
    public void Rejects_unknown_or_duplicate_fields(string suffix) => Reject(Record("x") + suffix);

    [Fact]
    public void Requires_blank_record_boundary() => Reject(Record("one") + Record("two"));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("11111111111111111111111111111111")]
    public void Requires_nonempty_exact_D_uuid(string id) => Reject($"infobase : {id}\nname : x\n");

    [Fact]
    public void Rejects_duplicate_uuids_and_case_insensitive_names()
    {
        var id = Guid.NewGuid();
        Reject(Record("one", id) + "\n" + Record("two", id));
        Reject(Record("one") + "\n" + Record("one"));
        Reject(Record("БАЗА") + "\n" + Record("база"));
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\u001b")]
    [InlineData("\u007f")]
    [InlineData("\u0085")]
    [InlineData("\r")]
    [InlineData("a\tb")]
    public void Rejects_controls_in_values_or_bare_CR(string value)
    {
        Reject(Record("a" + value + "b"));
        Reject(Record("x", descr: "a" + value + "b"));
    }

    [Fact]
    public void Enforces_name_and_description_bounds()
    {
        var s = Scope();
        Assert.Single(Parse(s, Record(new string('界', 128), descr: new string('界', 4096))).Entries);
        Reject(Record(new string('a', 129)));
        Reject(Record("x", descr: new string('a', 4097)));
    }

    [Fact]
    public void Enforces_record_bound()
    {
        var text = string.Join("\n", Enumerable.Range(0, 256).Select(i => Record("name" + i)));
        Assert.Equal(256, Parse(Scope(), text).Entries.Count);
        Reject(text + "\n" + Record("overflow"));
    }

    [Fact]
    public void Enforces_total_output_bound_including_whitespace()
    {
        Assert.Empty(Parse(Scope(), new string(' ', 65536)).Entries);
        Reject(new string(' ', 65537));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public void Requires_success_and_untruncated_output(int exitCode, bool truncated)
    {
        var s = Scope();
        Assert.Equal("InfobaseInventoryReadbackUnavailable", Assert.Throws<InfobaseInventoryReadbackException>(() =>
            InfobaseInventoryReadback.Parse(s, exitCode, truncated, Record(s.ExpectedInfobaseName))).Code);
    }

    [Fact]
    public void All_scope_pins_must_match()
    {
        var s = Scope(); var result = Parse(s, ""); var reservation = Guid.NewGuid();
        foreach (var changed in new[] { s with { OperationId = Guid.NewGuid() }, s with { FencingToken = 8 },
            s with { TargetIncarnationId = Guid.NewGuid() }, s with { ClusterId = Guid.NewGuid() },
            s with { EndpointMappingId = Guid.NewGuid() }, s with { ObservationId = Guid.NewGuid() },
            s with { ReservationId = reservation, ExpectedInfobaseName = $"bs_test_{reservation:N}" } })
            Assert.Equal("InfobaseInventoryScopeMismatch", Assert.Throws<InfobaseInventoryReadbackException>(() =>
                InfobaseInventoryReadback.RequireMatch(result, changed)).Code);
    }

    [Fact]
    public void Rejects_invalid_scope_pins_and_non_generated_target_names()
    {
        var s = Scope();
        foreach (var invalid in new[] { s with { OperationId = Guid.Empty }, s with { FencingToken = 0 },
            s with { FencingToken = -1 }, s with { TargetIncarnationId = Guid.Empty },
            s with { ClusterId = Guid.Empty }, s with { EndpointMappingId = Guid.Empty },
            s with { ReservationId = Guid.Empty }, s with { ObservationId = Guid.Empty },
            s with { ExpectedInfobaseName = "" }, s with { ExpectedInfobaseName = null! },
            s with { ExpectedInfobaseName = "bs_test_" + Guid.NewGuid().ToString("N") },
            s with { ExpectedInfobaseName = s.ExpectedInfobaseName.ToUpperInvariant() } })
            Assert.Equal("InvalidInfobaseInventoryScope", Assert.Throws<InfobaseInventoryReadbackException>(() => Parse(invalid, "")).Code);
    }

    [Fact]
    public void Null_inputs_have_fixed_safe_errors()
    {
        var s = Scope();
        Assert.Throws<InfobaseInventoryReadbackException>(() => Parse(null!, ""));
        Assert.Throws<InfobaseInventoryReadbackException>(() => Parse(s, null!));
        Assert.Throws<InfobaseInventoryReadbackException>(() => InfobaseInventoryReadback.RequireMatch(null!, s));
        Assert.Throws<InfobaseInventoryReadbackException>(() => InfobaseInventoryReadback.RequireMatch(Parse(s, ""), null!));
    }

    [Fact]
    public void Result_entries_are_readonly_and_not_an_exposed_array()
    {
        var result = Parse(Scope(), Record("x"));
        Assert.False(result.Entries is InfobaseInventoryEntry[]);
        Assert.Throws<NotSupportedException>(() => ((IList)result.Entries)[0] = result.Entries[0]);
        Assert.Empty(typeof(InfobaseInventoryEntry).GetConstructors());
        Assert.Empty(typeof(InfobaseInventoryReadbackResult).GetConstructors());
        Assert.All(typeof(InfobaseInventoryEntry).GetProperties(), p => Assert.Null(p.SetMethod));
    }

    [Fact]
    public void Parser_does_not_authenticate_labels_or_prevent_exact_replay()
    {
        var s = Scope(); var text = Record(s.ExpectedInfobaseName); var first = Parse(s, text);
        var relabelled = s with { OperationId = Guid.NewGuid(), EndpointMappingId = Guid.NewGuid(), ClusterId = Guid.NewGuid() };
        InfobaseInventoryReadback.RequireMatch(Parse(relabelled, text), relabelled);
        InfobaseInventoryReadback.RequireMatch(first, s);
        InfobaseInventoryReadback.RequireMatch(first, s);
        Assert.Equal(first.Observation, Parse(s, text).Observation);
    }

    [Theory]
    [InlineData("  ", "")]
    [InlineData(" ", " ")]
    [InlineData("\t", "")]
    [InlineData(" ", "\t")]
    [InlineData(" \u00a0", "")]
    [InlineData(" ", "\u00a0")]
    public void Never_normalizes_ambiguous_target_name_whitespace(string before, string after)
    {
        var s = Scope();
        Reject($"infobase : {Guid.NewGuid():D}\nname :{before}{s.ExpectedInfobaseName}{after}\n");
    }

    [Theory]
    [InlineData("infobase")]
    [InlineData("descr")]
    public void Rejects_boundary_tabs_in_all_field_values(string key)
    {
        var id = Guid.NewGuid();
        Reject(key == "infobase" ? $"infobase : {id:D}\t\nname : x\n" : Record("x") + "descr : hidden\t\n");
    }

    [Theory]
    [InlineData("name:value with spaces")]
    [InlineData("name: value with spaces")]
    public void Keeps_interior_name_spaces_and_accepts_optional_single_separator(string nameField)
    {
        var result = Parse(Scope(), $"infobase : {Guid.NewGuid():D}\n{nameField}\n");
        Assert.Equal("value with spaces", Assert.Single(result.Entries).Name);
    }

    [Fact]
    public void Diagnostics_discard_raw_values_and_surface_has_no_authority()
    {
        var s = Scope(); var result = Parse(s, Record("SECRET_NAME", descr: "SECRET_DESCRIPTION"));
        Assert.DoesNotContain(s.ExpectedInfobaseName, s.ToString());
        Assert.DoesNotContain("SECRET", result.ToString());
        Assert.DoesNotContain("SECRET", result.Entries[0].ToString());
        var ex = Assert.Throws<InfobaseInventoryReadbackException>(() => Parse(s, "SECRET_ENDPOINT_PASSWORD"));
        Assert.Equal("InvalidInfobaseInventoryReadback", ex.Code);
        Assert.DoesNotContain("SECRET", ex.ToString());
        Assert.Equal(new[] { "Entries", "Observation", "Scope" },
            typeof(InfobaseInventoryReadbackResult).GetProperties().Select(p => p.Name).OrderBy(x => x).ToArray());
    }
}
